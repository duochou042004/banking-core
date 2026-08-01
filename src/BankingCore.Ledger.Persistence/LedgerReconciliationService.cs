using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BankingCore.Ledger.Persistence;

/// <summary>One recorded difference between facts that must agree.</summary>
/// <param name="BreakId">Identifier of the durable break record.</param>
/// <param name="CheckName">Which proof failed.</param>
/// <param name="Severity">How urgently it must be resolved.</param>
/// <param name="Subject">The identifier the difference is about.</param>
/// <param name="Detail">Structured evidence, safe to store and read.</param>
public sealed record ReconciliationBreak(
    Guid BreakId,
    string CheckName,
    string Severity,
    string Subject,
    string Detail);

/// <summary>Outcome of one reconciliation run.</summary>
/// <param name="RunId">Identifier of the run record.</param>
/// <param name="ChecksExecuted">How many proofs were evaluated.</param>
/// <param name="Breaks">The differences found, if any.</param>
public sealed record ReconciliationResult(Guid RunId, int ChecksExecuted, IReadOnlyList<ReconciliationBreak> Breaks)
{
    /// <summary>True when every proof held.</summary>
    public bool IsClean => Breaks.Count == 0;
}

/// <summary>
/// Continuously proves the ledger's internal consistency.
/// </summary>
/// <remarks>
/// <para>
/// Every proof recomputes an answer from the immutable postings and compares it with a stored or
/// derived value. A difference creates a durable break with severity, subject, and evidence; nothing
/// here repairs a balance or rewrites history (docs/architecture/ledger.md, "Reconciliation and
/// proofs"; evaluations AG-002, AG-014).
/// </para>
/// <para>
/// Runs are tenant-scoped, matching the row level security binding. External settlement and general
/// ledger control-total reconciliation belong to Phases 3 and 4 and are deliberately absent here.
/// </para>
/// </remarks>
public sealed class LedgerReconciliationService
{
    private static readonly JsonSerializerOptions DetailOptions = new(JsonSerializerDefaults.Web);

    private readonly LedgerDataSources _dataSources;
    private readonly LedgerDatabaseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LedgerReconciliationService> _logger;

    /// <summary>Creates the service.</summary>
    public LedgerReconciliationService(
        LedgerDataSources dataSources,
        IOptions<LedgerDatabaseOptions> options,
        TimeProvider timeProvider,
        ILogger<LedgerReconciliationService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataSources = dataSources;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The proofs this service evaluates, in execution order.</summary>
    public static IReadOnlyList<ReconciliationCheck> Checks { get; } =
    [
        new ReconciliationCheck(
            "journal-balances-per-asset",
            "critical",
            """
            SELECT p.journal_id::text AS subject,
                   jsonb_build_object(
                       'assetId', p.asset_id,
                       'debitTotal', coalesce(sum(p.amount) FILTER (WHERE p.direction = 'debit'), 0)::text,
                       'creditTotal', coalesce(sum(p.amount) FILTER (WHERE p.direction = 'credit'), 0)::text)::text AS detail
            FROM ledger.posting p
            JOIN ledger.journal j ON j.journal_id = p.journal_id
            WHERE (@ledger_id::uuid IS NULL OR j.ledger_id = @ledger_id)
            GROUP BY p.journal_id, p.ledger_id, p.asset_id
            HAVING coalesce(sum(p.amount) FILTER (WHERE p.direction = 'debit'), 0)
                <> coalesce(sum(p.amount) FILTER (WHERE p.direction = 'credit'), 0)
            """),

        new ReconciliationCheck(
            "aggregates-match-postings",
            "critical",
            """
            SELECT b.account_id::text AS subject,
                   jsonb_build_object(
                       'storedDebitTotal', b.debit_total::text,
                       'recomputedDebitTotal', coalesce(r.debit_total, 0)::text,
                       'storedCreditTotal', b.credit_total::text,
                       'recomputedCreditTotal', coalesce(r.credit_total, 0)::text,
                       'storedPostingCount', b.posting_count,
                       'recomputedPostingCount', coalesce(r.posting_count, 0))::text AS detail
            FROM ledger.account_balance b
            LEFT JOIN (
                SELECT p.account_id,
                       coalesce(sum(p.amount) FILTER (WHERE p.direction = 'debit'), 0) AS debit_total,
                       coalesce(sum(p.amount) FILTER (WHERE p.direction = 'credit'), 0) AS credit_total,
                       count(*) AS posting_count
                FROM ledger.posting p
                GROUP BY p.account_id
            ) r ON r.account_id = b.account_id
            WHERE (@ledger_id::uuid IS NULL OR b.ledger_id = @ledger_id)
              AND (b.debit_total <> coalesce(r.debit_total, 0)
                OR b.credit_total <> coalesce(r.credit_total, 0)
                OR b.posting_count <> coalesce(r.posting_count, 0))
            """),

        new ReconciliationCheck(
            "posting-account-compatibility",
            "critical",
            """
            SELECT p.posting_id::text AS subject,
                   jsonb_build_object('accountId', p.account_id, 'assetId', p.asset_id)::text AS detail
            FROM ledger.posting p
            LEFT JOIN ledger.ledger_account a
                ON a.account_id = p.account_id
               AND a.ledger_id = p.ledger_id
               AND a.tenant_id = p.tenant_id
               AND a.asset_id = p.asset_id
            JOIN ledger.journal j ON j.journal_id = p.journal_id
            WHERE (@ledger_id::uuid IS NULL OR j.ledger_id = @ledger_id)
              AND a.account_id IS NULL
            """),

        new ReconciliationCheck(
            "outbox-coverage",
            "high",
            """
            SELECT j.journal_id::text AS subject,
                   jsonb_build_object('ledgerSequence', j.ledger_sequence)::text AS detail
            FROM ledger.journal j
            LEFT JOIN ledger.outbox_message o
                ON o.journal_id = j.journal_id
               AND o.event_type = 'banking-core.ledger.journal.posted'
            WHERE (@ledger_id::uuid IS NULL OR j.ledger_id = @ledger_id)
              AND o.message_id IS NULL
            """),

        new ReconciliationCheck(
            "reversal-links-valid",
            "critical",
            """
            SELECT r.journal_id::text AS subject,
                   jsonb_build_object(
                       'reversesJournalId', r.reverses_journal_id,
                       'originalExists', (o.journal_id IS NOT NULL),
                       'originalIsReversal', (o.reverses_journal_id IS NOT NULL),
                       'sequenceOrdered', (o.ledger_sequence IS NOT NULL AND r.ledger_sequence > o.ledger_sequence))::text AS detail
            FROM ledger.journal r
            LEFT JOIN ledger.journal o ON o.journal_id = r.reverses_journal_id
            WHERE r.reverses_journal_id IS NOT NULL
              AND (@ledger_id::uuid IS NULL OR r.ledger_id = @ledger_id)
              AND (o.journal_id IS NULL
                OR o.reverses_journal_id IS NOT NULL
                OR o.ledger_id <> r.ledger_id
                OR r.ledger_sequence <= o.ledger_sequence)
            """),

        new ReconciliationCheck(
            "ledger-sequence-integrity",
            "critical",
            """
            SELECT s.ledger_id::text AS subject,
                   jsonb_build_object(
                       'journalCount', s.journal_count,
                       'maxSequence', s.max_sequence,
                       'distinctSequences', s.distinct_sequences)::text AS detail
            FROM (
                SELECT j.ledger_id,
                       count(*) AS journal_count,
                       max(j.ledger_sequence) AS max_sequence,
                       count(DISTINCT j.ledger_sequence) AS distinct_sequences
                FROM ledger.journal j
                WHERE (@ledger_id::uuid IS NULL OR j.ledger_id = @ledger_id)
                GROUP BY j.ledger_id
            ) s
            WHERE s.journal_count <> s.max_sequence OR s.distinct_sequences <> s.journal_count
            """),

        new ReconciliationCheck(
            "statement-projection-matches-postings",
            "high",
            """
            SELECT coalesce(p.posting_id, e.posting_id)::text AS subject,
                   jsonb_build_object(
                       'inLedger', (p.posting_id IS NOT NULL),
                       'inProjection', (e.posting_id IS NOT NULL),
                       'ledgerAmount', p.amount::text,
                       'projectedAmount', e.amount::text)::text AS detail
            FROM (
                SELECT p.posting_id, p.amount, p.direction, p.account_id
                FROM ledger.posting p
                JOIN ledger.journal j ON j.journal_id = p.journal_id
                JOIN ledger_projection.projection_checkpoint c
                    ON c.ledger_id = j.ledger_id AND c.projection_name = 'account-statement'
                WHERE j.ledger_sequence <= c.last_ledger_sequence
                  AND (@ledger_id::uuid IS NULL OR j.ledger_id = @ledger_id)
            ) p
            FULL OUTER JOIN (
                SELECT e.posting_id, e.amount, e.direction, e.account_id
                FROM ledger_projection.statement_entry e
                WHERE (@ledger_id::uuid IS NULL OR e.ledger_id = @ledger_id)
            ) e ON e.posting_id = p.posting_id
            WHERE p.posting_id IS NULL
               OR e.posting_id IS NULL
               OR p.amount <> e.amount
               OR p.direction <> e.direction
               OR p.account_id <> e.account_id
            """),
    ];

    /// <summary>
    /// Runs every proof for one tenant, optionally narrowed to one ledger, and records both the run
    /// and any breaks it found.
    /// </summary>
    /// <param name="tenantId">Tenant to reconcile.</param>
    /// <param name="ledgerId">Optional ledger filter; <see langword="null"/> reconciles all of the tenant's ledgers.</param>
    /// <param name="sourceRevision">The build or commit identifier that produced this evidence.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ReconciliationResult> RunAsync(
        Guid tenantId,
        Guid? ledgerId = null,
        string sourceRevision = "unspecified",
        CancellationToken cancellationToken = default) =>
        LedgerUnitOfWork.ExecuteAsync(
            _dataSources.For(LedgerRole.Projection),
            tenantId,
            IsolationLevel.RepeatableRead,
            _options.MaxSerializationRetries,
            _options.SerializationRetryBaseDelay,
            _logger,
            async (connection, transaction, token) =>
            {
                var runId = Guid.NewGuid();
                var startedAt = _timeProvider.GetUtcNow();
                await InsertRunAsync(connection, transaction, runId, tenantId, ledgerId, startedAt, sourceRevision, token)
                    .ConfigureAwait(false);

                var breaks = new List<ReconciliationBreak>();
                foreach (var check in Checks)
                {
                    var findings = await RunCheckAsync(connection, transaction, check, ledgerId, token)
                        .ConfigureAwait(false);
                    foreach (var (subject, detail) in findings)
                    {
                        var breakId = Guid.NewGuid();
                        await InsertBreakAsync(
                            connection, transaction, breakId, runId, tenantId, check, subject, detail, token)
                            .ConfigureAwait(false);
                        breaks.Add(new ReconciliationBreak(breakId, check.Name, check.Severity, subject, detail));
                    }
                }

                await CompleteRunAsync(connection, transaction, runId, Checks.Count, breaks.Count, token)
                    .ConfigureAwait(false);

                if (breaks.Count > 0)
                {
                    _logger.LogError(
                        "Ledger reconciliation run {RunId} found {BreakCount} break(s). This is a stop-the-line "
                        + "condition: preserve evidence and investigate before further processing.",
                        runId,
                        breaks.Count);
                }

                return new ReconciliationResult(runId, Checks.Count, breaks);
            },
            cancellationToken);

    private static async Task<List<(string Subject, string Detail)>> RunCheckAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ReconciliationCheck check,
        Guid? ledgerId,
        CancellationToken cancellationToken)
    {
        var findings = new List<(string, string)>();
        await using var command = new NpgsqlCommand(check.Sql, connection, transaction);
        command.Parameters.AddWithValue("ledger_id", (object?)ledgerId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            findings.Add((reader.GetString(0), reader.GetString(1)));
        }

        return findings;
    }

    private static async Task InsertRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        Guid tenantId,
        Guid? ledgerId,
        DateTimeOffset startedAt,
        string sourceRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger.reconciliation_run (run_id, tenant_id, ledger_id, started_at, source_revision)
            VALUES (@run_id, @tenant_id, @ledger_id, @started_at, @source_revision)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("ledger_id", (object?)ledgerId ?? DBNull.Value);
        command.Parameters.AddWithValue("started_at", startedAt);
        command.Parameters.AddWithValue("source_revision", sourceRevision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertBreakAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid breakId,
        Guid runId,
        Guid tenantId,
        ReconciliationCheck check,
        string subject,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger.reconciliation_break (
                break_id, run_id, tenant_id, check_name, severity, subject, detail, status, detected_at)
            VALUES (@break_id, @run_id, @tenant_id, @check_name, @severity, @subject, @detail::jsonb, 'open', @detected_at)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("break_id", breakId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("check_name", check.Name);
        command.Parameters.AddWithValue("severity", check.Severity);
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("detail", detail);
        command.Parameters.AddWithValue("detected_at", _timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        int checksExecuted,
        int breaksFound,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE ledger.reconciliation_run
            SET completed_at = @completed_at, checks_executed = @checks_executed, breaks_found = @breaks_found
            WHERE run_id = @run_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("completed_at", _timeProvider.GetUtcNow());
        command.Parameters.AddWithValue("checks_executed", checksExecuted);
        command.Parameters.AddWithValue("breaks_found", breaksFound);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serializes structured break evidence.</summary>
    public static string SerializeDetail<T>(T value) => JsonSerializer.Serialize(value, DetailOptions);
}

/// <summary>One named consistency proof.</summary>
/// <param name="Name">Stable check name recorded on every break.</param>
/// <param name="Severity">Severity assigned to breaks this check produces.</param>
/// <param name="Sql">
/// A query returning <c>(subject text, detail text)</c> rows, one per difference, and no rows when
/// the proof holds. It accepts a single <c>@ledger_id</c> parameter that may be NULL.
/// </param>
public sealed record ReconciliationCheck(string Name, string Severity, string Sql);
