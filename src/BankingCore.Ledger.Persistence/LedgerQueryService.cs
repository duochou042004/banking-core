using System.Data;
using BankingCore.Ledger.Model;
using BankingCore.Ledger.Money;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BankingCore.Ledger.Persistence;

/// <summary>An account with its authoritative aggregates and derived balances.</summary>
/// <param name="Account">The account definition.</param>
/// <param name="Asset">The account's asset.</param>
/// <param name="Balance">Authoritative debit and credit aggregates.</param>
/// <param name="AsOf">When the aggregates were read.</param>
public sealed record AccountBalanceView(
    LedgerAccount Account,
    Asset Asset,
    AccountBalance Balance,
    DateTimeOffset AsOf)
{
    /// <summary>Posted balance in atomic units, signed against the account's normal side.</summary>
    public Int128 PostedBalance => Balance.PostedBalance(Account.NormalSide);

    /// <summary>
    /// Available balance in atomic units under the slice-1 policy, which has no holds or credit
    /// lines. Account Servicing replaces this calculation in Phase 2.
    /// </summary>
    public Int128 AvailableBalance => Balance.AvailableBalance(Account.NormalSide);
}

/// <summary>One line of an account statement, read from the derived projection.</summary>
/// <param name="PostingId">The posting this line reports.</param>
/// <param name="JournalId">The journal the posting belongs to.</param>
/// <param name="LedgerSequence">Commit order within the ledger.</param>
/// <param name="PostingOrder">Position within the journal.</param>
/// <param name="Direction">Debit or credit.</param>
/// <param name="Amount">Exact amount in atomic units.</param>
/// <param name="RunningDebitTotal">Account debit total after this line.</param>
/// <param name="RunningCreditTotal">Account credit total after this line.</param>
/// <param name="TransactionType">Business meaning of the journal.</param>
/// <param name="ReversesJournalId">Set when the line belongs to a reversal.</param>
/// <param name="BookingDate">Accounting date.</param>
/// <param name="EffectiveAt">When the economic event took effect.</param>
public sealed record StatementLine(
    Guid PostingId,
    Guid JournalId,
    long LedgerSequence,
    int PostingOrder,
    PostingDirection Direction,
    Amount Amount,
    Amount RunningDebitTotal,
    Amount RunningCreditTotal,
    string TransactionType,
    Guid? ReversesJournalId,
    DateOnly BookingDate,
    DateTimeOffset EffectiveAt);

/// <summary>
/// The query path.
/// </summary>
/// <remarks>
/// Runs under <c>banking_core_readonly</c>. Account aggregates are read from the authoritative
/// tables and are current as of the reading transaction; statement lines come from a derived
/// projection and are explicitly non-authoritative for a financial decision
/// (docs/architecture/integration.md, "Contract principles").
/// </remarks>
public sealed class LedgerQueryService
{
    private readonly LedgerDataSources _dataSources;
    private readonly LedgerDatabaseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LedgerQueryService> _logger;

    /// <summary>Creates the service.</summary>
    public LedgerQueryService(
        LedgerDataSources dataSources,
        IOptions<LedgerDatabaseOptions> options,
        TimeProvider timeProvider,
        ILogger<LedgerQueryService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataSources = dataSources;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Reads one account's authoritative aggregates. Returns <see langword="null"/> when the account
    /// does not exist within the bound tenant, so a cross-tenant identifier is indistinguishable
    /// from an unknown one (evaluation AG-011).
    /// </summary>
    public Task<AccountBalanceView?> GetAccountBalanceAsync(
        Guid tenantId,
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(tenantId, async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT a.account_id, a.ledger_id, a.tenant_id, a.legal_entity_id, a.code, a.asset_id,
                       a.account_class, a.normal_side, a.status, a.balance_policy,
                       s.code, s.scale, s.status, s.external_standard, s.external_code,
                       b.debit_total::text, b.credit_total::text, b.posting_count, b.version
                FROM ledger.ledger_account a
                JOIN ledger.asset s ON s.asset_id = a.asset_id
                JOIN ledger.account_balance b ON b.account_id = a.account_id
                WHERE a.account_id = @account_id
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("account_id", accountId);

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            var account = new LedgerAccount(
                reader.GetGuid(0),
                reader.GetGuid(1),
                new LedgerScope(reader.GetGuid(2), reader.GetGuid(3)),
                reader.GetString(4),
                reader.GetGuid(5),
                LedgerEnumTokens.ParseAccountClass(reader.GetString(6)),
                LedgerEnumTokens.ParseDirection(reader.GetString(7)),
                LedgerEnumTokens.ParseAccountStatus(reader.GetString(8)),
                BalancePolicy.FromName(reader.GetString(9)));

            var asset = new Asset(
                reader.GetGuid(5),
                reader.GetString(10),
                AssetScale.FromInt32(reader.GetInt16(11)),
                LedgerEnumTokens.ParseAssetStatus(reader.GetString(12)),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14));

            var balance = new AccountBalance(
                account.AccountId,
                account.AssetId,
                reader.GetAmount(15),
                reader.GetAmount(16),
                reader.GetInt64(17),
                reader.GetInt64(18));

            return new AccountBalanceView(account, asset, balance, _timeProvider.GetUtcNow());
        }, cancellationToken);

    /// <summary>Reads a posted journal and its postings, or <see langword="null"/> when out of scope.</summary>
    public Task<PostedJournal?> GetJournalAsync(
        Guid tenantId,
        Guid journalId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(tenantId, async (connection, transaction, token) =>
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
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
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
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
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
        }, cancellationToken);

    /// <summary>
    /// Reads a page of statement lines from the derived projection, ordered by commit position.
    /// The cursor is the exclusive lower bound on <c>(ledger_sequence, posting_order)</c>.
    /// </summary>
    public Task<IReadOnlyList<StatementLine>> GetStatementAsync(
        Guid tenantId,
        Guid accountId,
        long afterSequence,
        int afterPostingOrder,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, 500);
        return ExecuteAsync<IReadOnlyList<StatementLine>>(tenantId, async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT posting_id, journal_id, ledger_sequence, posting_order, direction,
                       amount::text, running_debit_total::text, running_credit_total::text,
                       transaction_type, reverses_journal_id, booking_date, effective_at
                FROM ledger_projection.statement_entry
                WHERE account_id = @account_id
                  AND (ledger_sequence, posting_order) > (@after_sequence, @after_posting_order)
                ORDER BY ledger_sequence, posting_order
                LIMIT @page_size
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("account_id", accountId);
            command.Parameters.AddWithValue("after_sequence", afterSequence);
            command.Parameters.AddWithValue("after_posting_order", (short)afterPostingOrder);
            command.Parameters.AddWithValue("page_size", pageSize);

            var lines = new List<StatementLine>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                lines.Add(new StatementLine(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetInt64(2),
                    reader.GetInt16(3),
                    LedgerEnumTokens.ParseDirection(reader.GetString(4)),
                    reader.GetAmount(5),
                    reader.GetAmount(6),
                    reader.GetAmount(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetGuid(9),
                    reader.GetFieldValue<DateOnly>(10),
                    reader.GetFieldValue<DateTimeOffset>(11)));
            }

            return lines;
        }, cancellationToken);
    }

    private Task<T> ExecuteAsync<T>(
        Guid tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken) =>
        LedgerUnitOfWork.ExecuteAsync(
            _dataSources.For(LedgerRole.ReadOnly),
            tenantId,
            IsolationLevel.ReadCommitted,
            _options.MaxSerializationRetries,
            _options.SerializationRetryBaseDelay,
            _logger,
            work,
            cancellationToken);
}
