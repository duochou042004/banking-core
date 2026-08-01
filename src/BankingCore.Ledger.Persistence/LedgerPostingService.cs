using System.Data;
using System.Text.Json;
using BankingCore.Ledger.Commands;
using BankingCore.Ledger.Idempotency;
using BankingCore.Ledger.Model;
using BankingCore.Ledger.Money;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BankingCore.Ledger.Persistence;

/// <summary>How a ledger command terminated.</summary>
public enum PostingOutcomeKind
{
    /// <summary>A new journal was committed by this call.</summary>
    Posted = 1,

    /// <summary>A previous identical command had already committed; its outcome is returned unchanged.</summary>
    IdempotentReplay = 2,

    /// <summary>The command was deterministically rejected; no journal exists.</summary>
    Rejected = 3,
}

/// <summary>The terminal outcome of a ledger command.</summary>
/// <param name="Kind">Whether the command posted, replayed, or was rejected.</param>
/// <param name="JournalId">The committed journal, when one exists.</param>
/// <param name="LedgerSequence">Its gap-free position within the ledger.</param>
/// <param name="PostedAt">Commit processing time.</param>
/// <param name="Error">The rejection, when the command was rejected.</param>
public sealed record PostingResult(
    PostingOutcomeKind Kind,
    Guid? JournalId,
    long? LedgerSequence,
    DateTimeOffset? PostedAt,
    LedgerError? Error);

/// <summary>
/// The authoritative posting path.
/// </summary>
/// <remarks>
/// <para>
/// One local database transaction commits or rolls back all of: the idempotency receipt and request
/// fingerprint, the journal and its postings, the authoritative aggregates and account versions, the
/// period state consulted for the decision, the audit record explaining the decision, and the outbox
/// row representing the committed fact (docs/architecture/ledger.md, "Atomic posting boundary").
/// No success is returned before that commit is durable.
/// </para>
/// <para>
/// Two concurrent commands sharing an idempotency scope and key are serialized by the receipt's
/// unique index rather than by an application lock: the loser blocks on the index until the winner
/// commits, then reads the committed outcome. This keeps the receipt inside the atomic boundary,
/// which a separate "in progress" pre-insert would break.
/// </para>
/// </remarks>
public sealed class LedgerPostingService
{
    private const string JournalPostedEventType = "banking-core.ledger.journal.posted";
    private const int JournalPostedEventVersion = 1;
    private const string EventSource = "banking-core/ledger";

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private readonly LedgerDataSources _dataSources;
    private readonly LedgerDatabaseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LedgerPostingService> _logger;

    /// <summary>Creates the service.</summary>
    public LedgerPostingService(
        LedgerDataSources dataSources,
        IOptions<LedgerDatabaseOptions> options,
        TimeProvider timeProvider,
        ILogger<LedgerPostingService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataSources = dataSources;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Posts one internal transfer between two accounts of the same ledger and asset.</summary>
    public async Task<PostingResult> PostInternalTransferAsync(
        InternalTransferCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var shapeError = command.ValidateShape();
        if (shapeError is not null)
        {
            // Structural rejections consult no ledger state and leave no trace, so they do not burn
            // the idempotency key: a client that corrects a malformed body may reuse it.
            return new PostingResult(PostingOutcomeKind.Rejected, null, null, null, shapeError);
        }

        var fingerprint = command.ComputeFingerprint();
        return await ExecuteCommandAsync(
            command.Idempotency,
            command.Scope,
            fingerprint,
            command.Authority,
            (_, _, _) => Task.FromResult(command.ToJournalDraft()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverses a posted journal by committing a new, independently balanced journal linked to the
    /// original. The original stays posted and is never edited.
    /// </summary>
    public async Task<PostingResult> ReverseJournalAsync(
        ReverseJournalCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var shapeError = command.ValidateShape();
        if (shapeError is not null)
        {
            return new PostingResult(PostingOutcomeKind.Rejected, null, null, null, shapeError);
        }

        var fingerprint = command.ComputeFingerprint();
        return await ExecuteCommandAsync(
            command.Idempotency,
            command.Scope,
            fingerprint,
            command.Authority,
            async (connection, transaction, token) =>
            {
                var original = await ReadJournalAsync(connection, transaction, command.JournalId, token)
                    .ConfigureAwait(false)
                    ?? throw new LedgerRejectedException(new LedgerError(
                        LedgerErrorCode.UnknownJournal,
                        "The journal to reverse does not exist in this scope."));

                if (original.ReversesJournalId is not null)
                {
                    throw new LedgerRejectedException(new LedgerError(
                        LedgerErrorCode.CannotReverseAReversal,
                        "A reversal cannot itself be reversed; post a compensating entry instead."));
                }

                if (original.ReversedByJournalId is not null)
                {
                    throw new LedgerRejectedException(new LedgerError(
                        LedgerErrorCode.JournalAlreadyReversed,
                        "The journal has already been reversed."));
                }

                return JournalValidator.BuildReversal(
                    original,
                    command.Dates,
                    command.Authority,
                    command.Reason,
                    command.CorrelationId);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PostingResult> ExecuteCommandAsync(
        IdempotencyScope scope,
        LedgerScope ledgerScope,
        byte[] fingerprint,
        CommandAuthority authority,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<JournalDraft>> draftFactory,
        CancellationToken cancellationToken)
    {
        var existing = await ReadReceiptAsync(scope, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return ResolveReceipt(existing, fingerprint);
        }

        try
        {
            return await LedgerUnitOfWork.ExecuteAsync(
                _dataSources.For(LedgerRole.Posting),
                ledgerScope.TenantId,
                IsolationLevel.Serializable,
                _options.MaxSerializationRetries,
                _options.SerializationRetryBaseDelay,
                _logger,
                (connection, transaction, token) =>
                    CommitAsync(connection, transaction, scope, ledgerScope, fingerprint, authority, draftFactory, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (LedgerRejectedException rejection)
        {
            return await RecordFailedReceiptAsync(scope, fingerprint, rejection.Error, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == LedgerUnitOfWork.UniqueViolation
            && exception.ConstraintName == "idempotency_receipt_scope_unique")
        {
            // A concurrent identical command won the race and has now committed. Its stored outcome
            // is the answer; this call must not post a second journal (evaluation AG-003).
            var committed = await ReadReceiptAsync(scope, cancellationToken).ConfigureAwait(false);
            return committed is not null
                ? ResolveReceipt(committed, fingerprint)
                : new PostingResult(
                    PostingOutcomeKind.Rejected,
                    null,
                    null,
                    null,
                    new LedgerError(
                        LedgerErrorCode.ConcurrencyRetryExhausted,
                        "A concurrent command holds this idempotency key; query the operation and retry."));
        }
        catch (PostgresException exception) when (exception.SqlState == LedgerUnitOfWork.UniqueViolation
            && exception.ConstraintName == "journal_single_reversal")
        {
            var error = new LedgerError(
                LedgerErrorCode.JournalAlreadyReversed,
                "The journal has already been reversed.");
            return await RecordFailedReceiptAsync(scope, fingerprint, error, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LedgerConcurrencyException exception)
        {
            _logger.LogWarning(exception, "The posting path exhausted its serialization retry budget.");
            return new PostingResult(
                PostingOutcomeKind.Rejected,
                null,
                null,
                null,
                new LedgerError(
                    LedgerErrorCode.ConcurrencyRetryExhausted,
                    "The ledger could not commit under contention; retry the same command with the same key."));
        }
    }

    private async Task<PostingResult> CommitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IdempotencyScope scope,
        LedgerScope ledgerScope,
        byte[] fingerprint,
        CommandAuthority authority,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<JournalDraft>> draftFactory,
        CancellationToken cancellationToken)
    {
        var draft = await draftFactory(connection, transaction, cancellationToken).ConfigureAwait(false);

        var accountIds = draft.Postings.Select(posting => posting.AccountId).Distinct().Order().ToArray();
        var accounts = await LockAccountsAsync(connection, transaction, accountIds, cancellationToken)
            .ConfigureAwait(false);

        var periodOpen = await IsPeriodOpenAsync(
            connection,
            transaction,
            draft.LedgerId,
            DateOnly.FromDateTime(draft.Dates.EffectiveAt.UtcDateTime),
            cancellationToken).ConfigureAwait(false);

        var validationError = JournalValidator.Validate(draft, accounts, periodOpen, out var validated);
        if (validationError is not null || validated is null)
        {
            throw new LedgerRejectedException(validationError!);
        }

        var journalId = Guid.NewGuid();
        var postedAt = _timeProvider.GetUtcNow();
        var sequence = await ReserveSequenceAsync(connection, transaction, draft.LedgerId, cancellationToken)
            .ConfigureAwait(false);

        await InsertJournalAsync(connection, transaction, journalId, sequence, postedAt, draft, scope, cancellationToken)
            .ConfigureAwait(false);
        await InsertPostingsAsync(connection, transaction, journalId, draft, accounts, cancellationToken)
            .ConfigureAwait(false);
        await ApplyAggregatesAsync(connection, transaction, validated.Deltas, postedAt, cancellationToken)
            .ConfigureAwait(false);
        await InsertAuditAsync(connection, transaction, journalId, draft, authority, postedAt, cancellationToken)
            .ConfigureAwait(false);
        await InsertOutboxAsync(
            connection, transaction, journalId, sequence, postedAt, draft, accounts, cancellationToken)
            .ConfigureAwait(false);
        await InsertReceiptAsync(
            connection,
            transaction,
            scope,
            fingerprint,
            IdempotencyOutcome.Succeeded,
            journalId,
            error: null,
            postedAt,
            cancellationToken).ConfigureAwait(false);

        return new PostingResult(PostingOutcomeKind.Posted, journalId, sequence, postedAt, null);
    }

    /// <summary>
    /// Reads and locks the authoritative aggregates for every account the journal touches, in
    /// ascending identifier order. Deterministic ordering reduces deadlocks; the retry loop in
    /// <see cref="LedgerUnitOfWork"/> is the backstop for the ones that still occur
    /// (docs/architecture/data-and-consistency.md, "Transaction policy").
    /// </summary>
    private static async Task<Dictionary<Guid, AccountPostingContext>> LockAccountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] accountIds,
        CancellationToken cancellationToken)
    {
        var contexts = new Dictionary<Guid, AccountPostingContext>();

        await using var command = new NpgsqlCommand(
            """
            SELECT b.account_id, b.debit_total::text, b.credit_total::text, b.posting_count, b.version,
                   a.ledger_id, a.tenant_id, a.legal_entity_id, a.code, a.asset_id,
                   a.account_class, a.normal_side, a.status, a.balance_policy,
                   s.code, s.scale, s.status, s.external_standard, s.external_code
            FROM ledger.account_balance b
            JOIN ledger.ledger_account a ON a.account_id = b.account_id
            JOIN ledger.asset s ON s.asset_id = a.asset_id
            WHERE b.account_id = ANY(@account_ids)
            ORDER BY b.account_id
            FOR NO KEY UPDATE OF b
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("account_ids", accountIds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var accountId = reader.GetGuid(0);
            var balance = new AccountBalance(
                accountId,
                reader.GetGuid(9),
                reader.GetAmount(1),
                reader.GetAmount(2),
                reader.GetInt64(3),
                reader.GetInt64(4));

            var account = new LedgerAccount(
                accountId,
                reader.GetGuid(5),
                new LedgerScope(reader.GetGuid(6), reader.GetGuid(7)),
                reader.GetString(8),
                reader.GetGuid(9),
                LedgerEnumTokens.ParseAccountClass(reader.GetString(10)),
                LedgerEnumTokens.ParseDirection(reader.GetString(11)),
                LedgerEnumTokens.ParseAccountStatus(reader.GetString(12)),
                BalancePolicy.FromName(reader.GetString(13)));

            var asset = new Asset(
                reader.GetGuid(9),
                reader.GetString(14),
                AssetScale.FromInt32(reader.GetInt16(15)),
                LedgerEnumTokens.ParseAssetStatus(reader.GetString(16)),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18));

            contexts[accountId] = new AccountPostingContext(account, asset, balance);
        }

        return contexts;
    }

    private static async Task<bool> IsPeriodOpenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ledgerId,
        DateOnly effectiveDate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT status
            FROM ledger.accounting_period
            WHERE ledger_id = @ledger_id AND @effective_date BETWEEN period_start AND period_end
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("ledger_id", ledgerId);
        command.Parameters.AddWithValue("effective_date", effectiveDate);

        var status = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return status is string text && text == "open";
    }

    private static async Task<long> ReserveSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ledgerId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE ledger.ledger_sequence_state
            SET next_sequence = next_sequence + 1
            WHERE ledger_id = @ledger_id
            RETURNING next_sequence - 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("ledger_id", ledgerId);

        var assigned = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return assigned is long sequence
            ? sequence
            : throw new LedgerRejectedException(new LedgerError(
                LedgerErrorCode.UnknownLedger,
                "The ledger does not exist in this scope."));
    }

    private static async Task InsertJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid journalId,
        long sequence,
        DateTimeOffset postedAt,
        JournalDraft draft,
        IdempotencyScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger.journal (
                journal_id, ledger_id, tenant_id, legal_entity_id, ledger_sequence,
                transaction_type, schema_version, reason, external_reference,
                command_id, correlation_id, causation_id,
                actor_id, actor_type, authorization_decision_id,
                posted_at, effective_at, booking_date, value_date, business_date, reverses_journal_id)
            VALUES (
                @journal_id, @ledger_id, @tenant_id, @legal_entity_id, @ledger_sequence,
                @transaction_type, @schema_version, @reason, @external_reference,
                @command_id, @correlation_id, @causation_id,
                @actor_id, @actor_type, @authorization_decision_id,
                @posted_at, @effective_at, @booking_date, @value_date, @business_date, @reverses_journal_id)
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("journal_id", journalId);
        command.Parameters.AddWithValue("ledger_id", draft.LedgerId);
        command.Parameters.AddWithValue("tenant_id", draft.Scope.TenantId);
        command.Parameters.AddWithValue("legal_entity_id", draft.Scope.LegalEntityId);
        command.Parameters.AddWithValue("ledger_sequence", sequence);
        command.Parameters.AddWithValue("transaction_type", draft.TransactionType);
        command.Parameters.AddWithValue("schema_version", draft.SchemaVersion);
        command.Parameters.AddWithValue("reason", draft.Reason);
        command.Parameters.AddWithValue("external_reference", (object?)draft.ExternalReference ?? DBNull.Value);
        command.Parameters.AddWithValue("command_id", DeriveCommandId(scope));
        command.Parameters.AddWithValue("correlation_id", draft.CorrelationId);
        command.Parameters.AddWithValue("causation_id", (object?)draft.CausationId ?? DBNull.Value);
        command.Parameters.AddWithValue("actor_id", draft.Authority.ActorId);
        command.Parameters.AddWithValue(
            "actor_type",
            draft.Authority.ActorType == ActorType.User ? "user" : "workload");
        command.Parameters.AddWithValue("authorization_decision_id", draft.Authority.AuthorizationDecisionId);
        command.Parameters.AddWithValue("posted_at", postedAt);
        command.Parameters.AddWithValue("effective_at", draft.Dates.EffectiveAt);
        command.Parameters.AddWithValue("booking_date", draft.Dates.BookingDate);
        command.Parameters.AddWithValue("value_date", draft.Dates.ValueDate);
        command.Parameters.AddWithValue("business_date", draft.Dates.BusinessDate);
        command.Parameters.AddWithValue("reverses_journal_id", (object?)draft.ReversesJournalId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertPostingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid journalId,
        JournalDraft draft,
        Dictionary<Guid, AccountPostingContext> accounts,
        CancellationToken cancellationToken)
    {
        foreach (var posting in draft.Postings)
        {
            var account = accounts[posting.AccountId].Account;
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO ledger.posting (
                    posting_id, journal_id, posting_order, account_id, ledger_id, tenant_id,
                    asset_id, direction, amount)
                VALUES (
                    @posting_id, @journal_id, @posting_order, @account_id, @ledger_id, @tenant_id,
                    @asset_id, @direction, @amount::numeric)
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("posting_id", Guid.NewGuid());
            command.Parameters.AddWithValue("journal_id", journalId);
            command.Parameters.AddWithValue("posting_order", (short)posting.PostingOrder);
            command.Parameters.AddWithValue("account_id", posting.AccountId);
            command.Parameters.AddWithValue("ledger_id", account.LedgerId);
            command.Parameters.AddWithValue("tenant_id", account.Scope.TenantId);
            command.Parameters.AddWithValue("asset_id", account.AssetId);
            command.Parameters.AddWithValue("direction", posting.Direction.ToToken());
            command.AddAmount("amount", posting.Amount);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the authoritative aggregates computed from the same postings that were inserted, so
    /// the two can never diverge within a transaction. Reconciliation independently recomputes them
    /// from the immutable postings afterwards.
    /// </summary>
    private static async Task ApplyAggregatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<AccountDelta> deltas,
        DateTimeOffset postedAt,
        CancellationToken cancellationToken)
    {
        foreach (var delta in deltas)
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE ledger.account_balance
                SET debit_total = @debit_total::numeric,
                    credit_total = @credit_total::numeric,
                    posting_count = posting_count + @posting_count,
                    version = version + 1,
                    updated_at = @updated_at
                WHERE account_id = @account_id
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("account_id", delta.AccountId);
            command.AddAmount("debit_total", delta.ResultingDebitTotal);
            command.AddAmount("credit_total", delta.ResultingCreditTotal);
            command.Parameters.AddWithValue("posting_count", delta.PostingCount);
            command.Parameters.AddWithValue("updated_at", postedAt);

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    "The authoritative aggregate row for a posted account was not updated exactly once.");
            }
        }
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid journalId,
        JournalDraft draft,
        CommandAuthority authority,
        DateTimeOffset postedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger.audit_event (
                audit_id, tenant_id, occurred_at, actor_id, actor_type, action,
                resource_type, resource_id, authorization_decision_id, outcome, correlation_id, detail)
            VALUES (
                @audit_id, @tenant_id, @occurred_at, @actor_id, @actor_type, @action,
                'journal', @resource_id, @authorization_decision_id, 'allowed', @correlation_id, @detail::jsonb)
            """,
            connection,
            transaction);

        var detail = JsonSerializer.Serialize(
            new
            {
                transactionType = draft.TransactionType,
                schemaVersion = draft.SchemaVersion,
                postingCount = draft.Postings.Count,
                reversesJournalId = draft.ReversesJournalId,
            },
            PayloadOptions);

        command.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenant_id", draft.Scope.TenantId);
        command.Parameters.AddWithValue("occurred_at", postedAt);
        command.Parameters.AddWithValue("actor_id", authority.ActorId);
        command.Parameters.AddWithValue("actor_type", authority.ActorType == ActorType.User ? "user" : "workload");
        command.Parameters.AddWithValue("action", "post-journal");
        command.Parameters.AddWithValue("resource_id", journalId.ToString());
        command.Parameters.AddWithValue("authorization_decision_id", authority.AuthorizationDecisionId);
        command.Parameters.AddWithValue("correlation_id", draft.CorrelationId);
        command.Parameters.AddWithValue("detail", detail);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the integration event in the same transaction as the fact. The payload carries opaque
    /// identifiers and exact amount strings only; no party name, reason text, or other potentially
    /// restricted value crosses the boundary (evaluation AG-012).
    /// </summary>
    private static async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid journalId,
        long sequence,
        DateTimeOffset postedAt,
        JournalDraft draft,
        Dictionary<Guid, AccountPostingContext> accounts,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                journalId,
                ledgerId = draft.LedgerId,
                tenantId = draft.Scope.TenantId,
                legalEntityId = draft.Scope.LegalEntityId,
                ledgerSequence = sequence,
                transactionType = draft.TransactionType,
                schemaVersion = draft.SchemaVersion,
                postedAt,
                effectiveAt = draft.Dates.EffectiveAt,
                bookingDate = draft.Dates.BookingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                valueDate = draft.Dates.ValueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                reversesJournalId = draft.ReversesJournalId,
                postings = draft.Postings.Select(posting => new
                {
                    postingOrder = posting.PostingOrder,
                    accountId = posting.AccountId,
                    assetId = accounts[posting.AccountId].Account.AssetId,
                    direction = posting.Direction.ToToken(),
                    amount = posting.Amount.ToString(),
                }),
            },
            PayloadOptions);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger.outbox_message (
                message_id, tenant_id, journal_id, event_type, event_schema_version, source, subject,
                partition_key, data_classification, correlation_id, causation_id, occurred_at, payload)
            VALUES (
                @message_id, @tenant_id, @journal_id, @event_type, @event_schema_version, @source, @subject,
                @partition_key, 'internal', @correlation_id, @causation_id, @occurred_at, @payload::jsonb)
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("message_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenant_id", draft.Scope.TenantId);
        command.Parameters.AddWithValue("journal_id", journalId);
        command.Parameters.AddWithValue("event_type", JournalPostedEventType);
        command.Parameters.AddWithValue("event_schema_version", JournalPostedEventVersion);
        command.Parameters.AddWithValue("source", EventSource);
        command.Parameters.AddWithValue("subject", journalId.ToString());
        command.Parameters.AddWithValue("partition_key", draft.LedgerId.ToString());
        command.Parameters.AddWithValue("correlation_id", draft.CorrelationId);
        command.Parameters.AddWithValue("causation_id", (object?)draft.CausationId ?? DBNull.Value);
        command.Parameters.AddWithValue("occurred_at", postedAt);
        command.Parameters.AddWithValue("payload", payload);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IdempotencyScope scope,
        byte[] fingerprint,
        IdempotencyOutcome outcome,
        Guid? journalId,
        LedgerError? error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger.idempotency_receipt (
                receipt_id, tenant_id, principal_id, operation, idempotency_key, request_fingerprint,
                outcome, outcome_journal_id, outcome_code, outcome_detail, created_at, expires_at)
            VALUES (
                @receipt_id, @tenant_id, @principal_id, @operation, @idempotency_key, @request_fingerprint,
                @outcome, @outcome_journal_id, @outcome_code, @outcome_detail, @created_at, @expires_at)
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("receipt_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenant_id", scope.TenantId);
        command.Parameters.AddWithValue("principal_id", scope.PrincipalId);
        command.Parameters.AddWithValue("operation", scope.Operation);
        command.Parameters.AddWithValue("idempotency_key", scope.Key);
        command.Parameters.AddWithValue("request_fingerprint", fingerprint);
        command.Parameters.AddWithValue("outcome", outcome == IdempotencyOutcome.Succeeded ? "succeeded" : "failed");
        command.Parameters.AddWithValue("outcome_journal_id", (object?)journalId ?? DBNull.Value);
        command.Parameters.AddWithValue("outcome_code", (object?)error?.Token ?? DBNull.Value);
        command.Parameters.AddWithValue("outcome_detail", (object?)error?.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("expires_at", now.Add(_options.IdempotencyRetention));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the terminal outcome of a deterministic rejection so a retry with the same key returns
    /// the same answer instead of re-evaluating against changed state, and returns the outcome the
    /// caller should be told.
    /// </summary>
    /// <remarks>
    /// The receipt is written in its own short transaction because the posting transaction rolled
    /// back. A concurrent command holding the same key may have committed in the meantime, in which
    /// case its receipt is authoritative and is returned instead of this rejection: reporting a
    /// rejection for a key that already has a committed journal would tell the caller something the
    /// ledger does not believe.
    /// </remarks>
    private async Task<PostingResult> RecordFailedReceiptAsync(
        IdempotencyScope scope,
        byte[] fingerprint,
        LedgerError error,
        CancellationToken cancellationToken)
    {
        try
        {
            await LedgerUnitOfWork.ExecuteAsync(
                _dataSources.For(LedgerRole.Posting),
                scope.TenantId,
                IsolationLevel.ReadCommitted,
                _options.MaxSerializationRetries,
                _options.SerializationRetryBaseDelay,
                _logger,
                async (connection, transaction, token) =>
                {
                    await InsertReceiptAsync(
                        connection,
                        transaction,
                        scope,
                        fingerprint,
                        IdempotencyOutcome.Failed,
                        journalId: null,
                        error,
                        _timeProvider.GetUtcNow(),
                        token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == LedgerUnitOfWork.UniqueViolation)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "A concurrent command already recorded a terminal outcome for operation {Operation}.",
                    scope.Operation);
            }

            var committed = await ReadReceiptAsync(scope, cancellationToken).ConfigureAwait(false);
            if (committed is not null)
            {
                return ResolveReceipt(committed, fingerprint);
            }
        }

        return new PostingResult(PostingOutcomeKind.Rejected, null, null, null, error);
    }

    private async Task<StoredReceipt?> ReadReceiptAsync(IdempotencyScope scope, CancellationToken cancellationToken) =>
        await LedgerUnitOfWork.ExecuteAsync(
            _dataSources.For(LedgerRole.Posting),
            scope.TenantId,
            IsolationLevel.ReadCommitted,
            _options.MaxSerializationRetries,
            _options.SerializationRetryBaseDelay,
            _logger,
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand(
                    """
                    SELECT r.request_fingerprint, r.outcome, r.outcome_journal_id, r.outcome_code,
                           r.outcome_detail, j.ledger_sequence, j.posted_at
                    FROM ledger.idempotency_receipt r
                    LEFT JOIN ledger.journal j ON j.journal_id = r.outcome_journal_id
                    WHERE r.tenant_id = @tenant_id
                      AND r.principal_id = @principal_id
                      AND r.operation = @operation
                      AND r.idempotency_key = @idempotency_key
                    """,
                    connection,
                    transaction);
                command.Parameters.AddWithValue("tenant_id", scope.TenantId);
                command.Parameters.AddWithValue("principal_id", scope.PrincipalId);
                command.Parameters.AddWithValue("operation", scope.Operation);
                command.Parameters.AddWithValue("idempotency_key", scope.Key);

                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return null;
                }

                return new StoredReceipt(
                    (byte[])reader.GetValue(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
            },
            cancellationToken).ConfigureAwait(false);

    private static PostingResult ResolveReceipt(StoredReceipt receipt, byte[] fingerprint)
    {
        if (!receipt.Fingerprint.AsSpan().SequenceEqual(fingerprint))
        {
            // Same scope and key, different request. The original outcome is preserved and the new
            // request is refused (evaluation AG-004).
            return new PostingResult(
                PostingOutcomeKind.Rejected,
                null,
                null,
                null,
                new LedgerError(
                    LedgerErrorCode.IdempotencyConflict,
                    "This idempotency key was already used for a different request."));
        }

        if (receipt.Outcome == "succeeded")
        {
            return new PostingResult(
                PostingOutcomeKind.IdempotentReplay,
                receipt.JournalId,
                receipt.LedgerSequence,
                receipt.PostedAt,
                null);
        }

        return new PostingResult(
            PostingOutcomeKind.Rejected,
            null,
            null,
            null,
            new LedgerError(ParseErrorCode(receipt.OutcomeCode), receipt.OutcomeDetail ?? "The command was rejected."));
    }

    private static LedgerErrorCode ParseErrorCode(string? token) => token switch
    {
        "journal-too-few-postings" => LedgerErrorCode.JournalTooFewPostings,
        "zero-posting-amount" => LedgerErrorCode.ZeroPostingAmount,
        "journal-not-balanced" => LedgerErrorCode.JournalNotBalanced,
        "duplicate-posting-order" => LedgerErrorCode.DuplicatePostingOrder,
        "unknown-account" => LedgerErrorCode.UnknownAccount,
        "account-ledger-mismatch" => LedgerErrorCode.AccountLedgerMismatch,
        "account-not-open" => LedgerErrorCode.AccountNotOpen,
        "asset-not-active" => LedgerErrorCode.AssetNotActive,
        "balance-policy-violation" => LedgerErrorCode.BalancePolicyViolation,
        "accounting-period-closed" => LedgerErrorCode.AccountingPeriodClosed,
        "unknown-journal" => LedgerErrorCode.UnknownJournal,
        "journal-already-reversed" => LedgerErrorCode.JournalAlreadyReversed,
        "cannot-reverse-a-reversal" => LedgerErrorCode.CannotReverseAReversal,
        "amount-out-of-range" => LedgerErrorCode.AmountOutOfRange,
        "unknown-ledger" => LedgerErrorCode.UnknownLedger,
        _ => LedgerErrorCode.MalformedRequest,
    };

    /// <summary>
    /// Derives the command identifier recorded on the journal from the idempotency scope, so the
    /// journal can be traced back to the exact command without storing the client key itself.
    /// </summary>
    private static Guid DeriveCommandId(IdempotencyScope scope)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{scope.TenantId:D}{scope.PrincipalId}{scope.Operation}{scope.Key}"));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static async Task<PostedJournal?> ReadJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid journalId,
        CancellationToken cancellationToken)
    {
        PostedJournal? journal = null;

        await using (var command = new NpgsqlCommand(
            """
            SELECT j.ledger_id, j.tenant_id, j.legal_entity_id, j.ledger_sequence, j.transaction_type,
                   j.reason, j.effective_at, j.booking_date, j.value_date, j.business_date, j.posted_at,
                   j.reverses_journal_id,
                   (SELECT r.journal_id FROM ledger.journal r WHERE r.reverses_journal_id = j.journal_id)
            FROM ledger.journal j
            WHERE j.journal_id = @journal_id
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("journal_id", journalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                journal = new PostedJournal(
                    journalId,
                    reader.GetGuid(0),
                    new LedgerScope(reader.GetGuid(1), reader.GetGuid(2)),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    new JournalDates(
                        reader.GetFieldValue<DateTimeOffset>(6),
                        reader.GetFieldValue<DateOnly>(7),
                        reader.GetFieldValue<DateOnly>(8),
                        reader.GetFieldValue<DateOnly>(9)),
                    reader.GetFieldValue<DateTimeOffset>(10),
                    reader.IsDBNull(11) ? null : reader.GetGuid(11),
                    reader.IsDBNull(12) ? null : reader.GetGuid(12),
                    []);
            }
        }

        if (journal is null)
        {
            return null;
        }

        var postings = new List<PostedPosting>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT posting_id, posting_order, account_id, asset_id, direction, amount::text
            FROM ledger.posting
            WHERE journal_id = @journal_id
            ORDER BY posting_order
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("journal_id", journalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                postings.Add(new PostedPosting(
                    reader.GetGuid(0),
                    reader.GetInt16(1),
                    reader.GetGuid(2),
                    reader.GetGuid(3),
                    LedgerEnumTokens.ParseDirection(reader.GetString(4)),
                    reader.GetAmount(5)));
            }
        }

        return journal with { Postings = postings };
    }

    private sealed record StoredReceipt(
        byte[] Fingerprint,
        string Outcome,
        Guid? JournalId,
        string? OutcomeCode,
        string? OutcomeDetail,
        long? LedgerSequence,
        DateTimeOffset? PostedAt);
}
