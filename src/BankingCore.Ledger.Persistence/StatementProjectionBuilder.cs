using System.Data;
using BankingCore.Ledger.Model;
using BankingCore.Ledger.Money;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BankingCore.Ledger.Persistence;

/// <summary>How far a projection advanced during one pass.</summary>
/// <param name="LedgerId">The ledger that was projected.</param>
/// <param name="FromSequence">Checkpoint before the pass.</param>
/// <param name="ToSequence">Checkpoint after the pass.</param>
/// <param name="EntriesWritten">Statement lines written.</param>
public sealed record ProjectionPassResult(Guid LedgerId, long FromSequence, long ToSequence, int EntriesWritten);

/// <summary>
/// Builds the account statement read model from committed ledger facts.
/// </summary>
/// <remarks>
/// <para>
/// Source: <c>ledger.journal</c> and <c>ledger.posting</c>, consumed in gap-free
/// <c>ledger_sequence</c> order. Checkpoint: <c>ledger_projection.projection_checkpoint</c>.
/// Consistency: eventually consistent and never authoritative for a financial decision. Rebuild:
/// <see cref="RebuildAsync"/> clears the entries for a ledger, resets the checkpoint, and replays.
/// </para>
/// <para>
/// The projection reads the ordered ledger directly rather than the outbox. Because the sequence is
/// dense, a checkpoint plus a replay is exact and needs no deduplication; the outbox exists for
/// external consumers, which do deduplicate.
/// </para>
/// </remarks>
public sealed class StatementProjectionBuilder
{
    /// <summary>The projection name recorded against each checkpoint.</summary>
    public const string ProjectionName = "account-statement";

    private readonly LedgerDataSources _dataSources;
    private readonly LedgerDatabaseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StatementProjectionBuilder> _logger;

    /// <summary>Creates the builder.</summary>
    public StatementProjectionBuilder(
        LedgerDataSources dataSources,
        IOptions<LedgerDatabaseOptions> options,
        TimeProvider timeProvider,
        ILogger<StatementProjectionBuilder> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataSources = dataSources;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Projects every journal committed after the current checkpoint.</summary>
    public Task<ProjectionPassResult> ProjectAsync(
        Guid tenantId,
        Guid ledgerId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(tenantId, async (connection, transaction, token) =>
        {
            var from = await ReadCheckpointAsync(connection, transaction, ledgerId, token).ConfigureAwait(false);
            var (written, to) = await ProjectFromAsync(connection, transaction, tenantId, ledgerId, from, token)
                .ConfigureAwait(false);
            await WriteCheckpointAsync(connection, transaction, tenantId, ledgerId, to, token).ConfigureAwait(false);
            return new ProjectionPassResult(ledgerId, from, to, written);
        }, cancellationToken);

    /// <summary>
    /// Discards and rebuilds the projection for one ledger. Only derived rows are removed; the
    /// authoritative journals, postings, and aggregates are never touched.
    /// </summary>
    public Task<ProjectionPassResult> RebuildAsync(
        Guid tenantId,
        Guid ledgerId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(tenantId, async (connection, transaction, token) =>
        {
            await using (var command = new NpgsqlCommand(
                "DELETE FROM ledger_projection.statement_entry WHERE ledger_id = @ledger_id",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("ledger_id", ledgerId);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            var (written, to) = await ProjectFromAsync(connection, transaction, tenantId, ledgerId, 0, token)
                .ConfigureAwait(false);
            await WriteCheckpointAsync(connection, transaction, tenantId, ledgerId, to, token).ConfigureAwait(false);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Rebuilt the {ProjectionName} projection for ledger {LedgerId} to sequence {Sequence}.",
                    ProjectionName,
                    ledgerId,
                    to);
            }

            return new ProjectionPassResult(ledgerId, 0, to, written);
        }, cancellationToken);

    private static async Task<(int Written, long ToSequence)> ProjectFromAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid ledgerId,
        long fromSequence,
        CancellationToken cancellationToken)
    {
        var running = await ReadRunningTotalsAsync(connection, transaction, ledgerId, fromSequence, cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<ProjectedRow>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT p.posting_id, p.account_id, p.asset_id, p.posting_order, p.direction, p.amount::text,
                   j.journal_id, j.ledger_sequence, j.transaction_type, j.reverses_journal_id,
                   j.booking_date, j.value_date, j.effective_at, j.posted_at
            FROM ledger.journal j
            JOIN ledger.posting p ON p.journal_id = j.journal_id
            WHERE j.ledger_id = @ledger_id AND j.ledger_sequence > @from_sequence
            ORDER BY j.ledger_sequence, p.posting_order
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("ledger_id", ledgerId);
            command.Parameters.AddWithValue("from_sequence", fromSequence);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ProjectedRow(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetInt16(3),
                    LedgerEnumTokens.ParseDirection(reader.GetString(4)),
                    reader.GetAmount(5),
                    reader.GetGuid(6),
                    reader.GetInt64(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetGuid(9),
                    reader.GetFieldValue<DateOnly>(10),
                    reader.GetFieldValue<DateOnly>(11),
                    reader.GetFieldValue<DateTimeOffset>(12),
                    reader.GetFieldValue<DateTimeOffset>(13)));
            }
        }

        var toSequence = fromSequence;
        foreach (var row in rows)
        {
            running.TryGetValue(row.AccountId, out var totals);
            totals = row.Direction == PostingDirection.Debit
                ? totals with { Debit = Amount.Add(totals.Debit, row.Amount) }
                : totals with { Credit = Amount.Add(totals.Credit, row.Amount) };
            running[row.AccountId] = totals;

            await InsertEntryAsync(connection, transaction, tenantId, ledgerId, row, totals, cancellationToken)
                .ConfigureAwait(false);
            toSequence = Math.Max(toSequence, row.LedgerSequence);
        }

        return (rows.Count, toSequence);
    }

    /// <summary>
    /// Recomputes each account's running totals up to the checkpoint, so an incremental pass
    /// continues the same series a full rebuild would produce.
    /// </summary>
    private static async Task<Dictionary<Guid, RunningTotals>> ReadRunningTotalsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ledgerId,
        long throughSequence,
        CancellationToken cancellationToken)
    {
        var totals = new Dictionary<Guid, RunningTotals>();
        if (throughSequence <= 0)
        {
            return totals;
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT p.account_id,
                   coalesce(sum(p.amount) FILTER (WHERE p.direction = 'debit'), 0)::text,
                   coalesce(sum(p.amount) FILTER (WHERE p.direction = 'credit'), 0)::text
            FROM ledger.journal j
            JOIN ledger.posting p ON p.journal_id = j.journal_id
            WHERE j.ledger_id = @ledger_id AND j.ledger_sequence <= @through_sequence
            GROUP BY p.account_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("ledger_id", ledgerId);
        command.Parameters.AddWithValue("through_sequence", throughSequence);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            totals[reader.GetGuid(0)] = new RunningTotals(reader.GetAmount(1), reader.GetAmount(2));
        }

        return totals;
    }

    private static async Task InsertEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid ledgerId,
        ProjectedRow row,
        RunningTotals totals,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger_projection.statement_entry (
                posting_id, tenant_id, ledger_id, account_id, asset_id, journal_id, ledger_sequence,
                posting_order, direction, amount, running_debit_total, running_credit_total,
                transaction_type, reverses_journal_id, booking_date, value_date, effective_at, posted_at)
            VALUES (
                @posting_id, @tenant_id, @ledger_id, @account_id, @asset_id, @journal_id, @ledger_sequence,
                @posting_order, @direction, @amount::numeric, @running_debit::numeric, @running_credit::numeric,
                @transaction_type, @reverses_journal_id, @booking_date, @value_date, @effective_at, @posted_at)
            ON CONFLICT (posting_id) DO NOTHING
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("posting_id", row.PostingId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("ledger_id", ledgerId);
        command.Parameters.AddWithValue("account_id", row.AccountId);
        command.Parameters.AddWithValue("asset_id", row.AssetId);
        command.Parameters.AddWithValue("journal_id", row.JournalId);
        command.Parameters.AddWithValue("ledger_sequence", row.LedgerSequence);
        command.Parameters.AddWithValue("posting_order", (short)row.PostingOrder);
        command.Parameters.AddWithValue("direction", row.Direction.ToToken());
        command.AddAmount("amount", row.Amount);
        command.AddAmount("running_debit", totals.Debit);
        command.AddAmount("running_credit", totals.Credit);
        command.Parameters.AddWithValue("transaction_type", row.TransactionType);
        command.Parameters.AddWithValue("reverses_journal_id", (object?)row.ReversesJournalId ?? DBNull.Value);
        command.Parameters.AddWithValue("booking_date", row.BookingDate);
        command.Parameters.AddWithValue("value_date", row.ValueDate);
        command.Parameters.AddWithValue("effective_at", row.EffectiveAt);
        command.Parameters.AddWithValue("posted_at", row.PostedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ledgerId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT last_ledger_sequence
            FROM ledger_projection.projection_checkpoint
            WHERE projection_name = @projection_name AND ledger_id = @ledger_id
            FOR UPDATE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("projection_name", ProjectionName);
        command.Parameters.AddWithValue("ledger_id", ledgerId);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long sequence ? sequence : 0L;
    }

    private async Task WriteCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid ledgerId,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ledger_projection.projection_checkpoint (
                projection_name, ledger_id, tenant_id, last_ledger_sequence, updated_at)
            VALUES (@projection_name, @ledger_id, @tenant_id, @last_ledger_sequence, @updated_at)
            ON CONFLICT (projection_name, ledger_id) DO UPDATE
            SET last_ledger_sequence = EXCLUDED.last_ledger_sequence,
                updated_at = EXCLUDED.updated_at
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("projection_name", ProjectionName);
        command.Parameters.AddWithValue("ledger_id", ledgerId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("last_ledger_sequence", sequence);
        command.Parameters.AddWithValue("updated_at", _timeProvider.GetUtcNow());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<T> ExecuteAsync<T>(
        Guid tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken) =>
        LedgerUnitOfWork.ExecuteAsync(
            _dataSources.For(LedgerRole.Projection),
            tenantId,
            IsolationLevel.ReadCommitted,
            _options.MaxSerializationRetries,
            _options.SerializationRetryBaseDelay,
            _logger,
            work,
            cancellationToken);

    private readonly record struct RunningTotals(Amount Debit, Amount Credit);

    private sealed record ProjectedRow(
        Guid PostingId,
        Guid AccountId,
        Guid AssetId,
        int PostingOrder,
        PostingDirection Direction,
        Amount Amount,
        Guid JournalId,
        long LedgerSequence,
        string TransactionType,
        Guid? ReversesJournalId,
        DateOnly BookingDate,
        DateOnly ValueDate,
        DateTimeOffset EffectiveAt,
        DateTimeOffset PostedAt);
}
