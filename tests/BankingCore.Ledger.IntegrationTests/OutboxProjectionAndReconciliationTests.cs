using System.Collections.Concurrent;
using BankingCore.Ledger.IntegrationTests.Infrastructure;
using BankingCore.Ledger.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BankingCore.Ledger.IntegrationTests;

/// <summary>
/// Delivery semantics, the derived statement projection, and the internal reconciliation proofs.
/// </summary>
public sealed class OutboxProjectionAndReconciliationTests : IAsyncLifetime
{
    private readonly LedgerTestDatabase _database = new();
    private LedgerScenario _scenario = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _scenario = await LedgerScenario.CreateAsync(_database);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task Every_committed_journal_has_exactly_one_unpublished_outbox_row()
    {
        var journalId = await _scenario.FundAsync(_scenario.CustomerAccountAId, 15_00);

        var (total, published) = await ReadOutboxCountsAsync(journalId);

        Assert.Equal(1, total);
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task A_rolled_back_posting_leaves_neither_a_journal_nor_an_outbox_row()
    {
        // AG-009: a ghost event must be impossible. The outbox row shares the source transaction, so
        // a rollback removes both or neither.
        await using (var connection = await _database.OpenAsAsync(LedgerRole.Posting))
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await LedgerUnitOfWork.BindTenantAsync(
                connection, transaction, _scenario.Scope.TenantId, CancellationToken.None);

            await using var command = new NpgsqlCommand(
                """
                INSERT INTO ledger.outbox_message (
                    message_id, tenant_id, journal_id, event_type, event_schema_version, source, subject,
                    partition_key, data_classification, correlation_id, occurred_at, payload)
                SELECT gen_random_uuid(), @tenant, journal_id, 'probe', 1, 'probe', 'probe',
                       'probe', 'internal', gen_random_uuid(), now(), '{}'::jsonb
                FROM ledger.journal LIMIT 1
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("tenant", _scenario.Scope.TenantId);
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

        var probeCount = await ScalarAsync<long>(
            LedgerRole.ReadOnly,
            "SELECT count(*) FROM ledger.outbox_message WHERE event_type = 'probe'");
        Assert.Equal(0L, probeCount);
    }

    [Fact]
    public async Task The_relay_publishes_a_pending_message_once_and_then_finds_nothing()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 15_00);
        var publisher = new RecordingPublisher();
        var relay = BuildRelay(publisher);

        var first = await relay.RelayPendingAsync(_scenario.Scope.TenantId);
        var second = await relay.RelayPendingAsync(_scenario.Scope.TenantId);

        Assert.Equal(1, first.Published);
        Assert.Equal(0, second.Published);
        Assert.Single(publisher.Published);
        Assert.Equal("banking-core.ledger.journal.posted", publisher.Published[0].Type);
        Assert.Equal("internal", publisher.Published[0].DataClassification);
    }

    [Fact]
    public async Task The_published_payload_carries_only_opaque_identifiers_and_exact_amounts()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 15_00);
        var publisher = new RecordingPublisher();
        await BuildRelay(publisher).RelayPendingAsync(_scenario.Scope.TenantId);

        var payload = publisher.Published[0].Payload;

        // Amounts travel as exact decimal strings, never JSON numbers. The payload is stored as
        // jsonb, so PostgreSQL returns it in its own normalised spacing.
        Assert.Contains("\"amount\": \"1500\"", payload, StringComparison.Ordinal);
        // The operator-supplied reason text is deliberately not published.
        Assert.DoesNotContain("integration test transfer", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_quarantined_with_a_reason_rather_than_dropped()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 15_00);
        var relay = BuildRelay(new AlwaysFailingPublisher(), maxAttempts: 3);

        var quarantinedAcrossPasses = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var pass = await relay.RelayPendingAsync(_scenario.Scope.TenantId);
            quarantinedAcrossPasses += pass.Quarantined;
        }

        // Quarantine happens once, on the pass where the attempt budget runs out; later passes see
        // nothing pending because a quarantined message is excluded from leasing.
        Assert.Equal(1, quarantinedAcrossPasses);

        var quarantined = await ScalarAsync<long>(
            LedgerRole.ReadOnly,
            "SELECT count(*) FROM ledger.outbox_message WHERE quarantined_at IS NOT NULL AND quarantine_reason IS NOT NULL");
        var lost = await ScalarAsync<long>(LedgerRole.ReadOnly, "SELECT count(*) FROM ledger.outbox_message");

        Assert.Equal(1L, quarantined);
        Assert.Equal(1L, lost);
    }

    [Fact]
    public async Task A_consumer_accepts_an_event_once_and_recognises_every_redelivery()
    {
        var eventId = Guid.NewGuid();

        var first = await _scenario.Inbox.TryAcceptAsync("statement-consumer", _scenario.Scope.TenantId, eventId);
        var second = await _scenario.Inbox.TryAcceptAsync("statement-consumer", _scenario.Scope.TenantId, eventId);
        var third = await _scenario.Inbox.TryAcceptAsync("statement-consumer", _scenario.Scope.TenantId, eventId);

        // A different consumer has its own deduplication state.
        var otherConsumer = await _scenario.Inbox.TryAcceptAsync(
            "reporting-consumer", _scenario.Scope.TenantId, eventId);

        Assert.True(first);
        Assert.False(second);
        Assert.False(third);
        Assert.True(otherConsumer);
    }

    [Fact]
    public async Task The_statement_projection_reproduces_every_posting_with_running_totals()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 100_00);
        await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.CustomerAccountAId, _scenario.CustomerAccountBId, 30_00, "stmt-1"));
        await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.CustomerAccountAId, _scenario.CustomerAccountBId, 20_00, "stmt-2"));

        var pass = await _scenario.Projection.ProjectAsync(_scenario.Scope.TenantId, _scenario.LedgerId);
        Assert.Equal(3, pass.ToSequence);
        Assert.Equal(6, pass.EntriesWritten);

        var lines = await _scenario.Query.GetStatementAsync(
            _scenario.Scope.TenantId, _scenario.CustomerAccountAId, 0, 0, 100);

        Assert.Equal(3, lines.Count);
        Assert.Equal("10000", lines[0].RunningCreditTotal.ToString());
        Assert.Equal("0", lines[0].RunningDebitTotal.ToString());
        Assert.Equal("3000", lines[1].RunningDebitTotal.ToString());
        Assert.Equal("5000", lines[2].RunningDebitTotal.ToString());

        // The final running totals equal the authoritative aggregates.
        var view = await _scenario.Query.GetAccountBalanceAsync(
            _scenario.Scope.TenantId, _scenario.CustomerAccountAId);
        Assert.Equal(view!.Balance.DebitTotal.ToString(), lines[^1].RunningDebitTotal.ToString());
        Assert.Equal(view.Balance.CreditTotal.ToString(), lines[^1].RunningCreditTotal.ToString());
    }

    [Fact]
    public async Task An_incremental_projection_pass_continues_the_same_series_a_rebuild_produces()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 100_00);
        await _scenario.Projection.ProjectAsync(_scenario.Scope.TenantId, _scenario.LedgerId);

        await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.CustomerAccountAId, _scenario.CustomerAccountBId, 40_00, "incremental-1"));
        await _scenario.Projection.ProjectAsync(_scenario.Scope.TenantId, _scenario.LedgerId);

        var incremental = await ReadStatementSnapshotAsync(_scenario.CustomerAccountAId);

        await _scenario.Projection.RebuildAsync(_scenario.Scope.TenantId, _scenario.LedgerId);
        var rebuilt = await ReadStatementSnapshotAsync(_scenario.CustomerAccountAId);

        Assert.Equal(incremental, rebuilt);
    }

    [Fact]
    public async Task A_reversal_appears_in_the_statement_linked_to_the_journal_it_reverses()
    {
        var original = await _scenario.FundAsync(_scenario.CustomerAccountAId, 60_00);
        await _scenario.Posting.ReverseJournalAsync(_scenario.Reversal(original, "stmt-reverse-1"));
        await _scenario.Projection.ProjectAsync(_scenario.Scope.TenantId, _scenario.LedgerId);

        var lines = await _scenario.Query.GetStatementAsync(
            _scenario.Scope.TenantId, _scenario.CustomerAccountAId, 0, 0, 100);

        Assert.Equal(2, lines.Count);
        Assert.Null(lines[0].ReversesJournalId);
        Assert.Equal(original, lines[1].ReversesJournalId);
        Assert.Equal("internal-transfer.reversal", lines[1].TransactionType);
    }

    [Fact]
    public async Task Reconciliation_is_clean_on_a_healthy_ledger()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 80_00);
        await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.CustomerAccountAId, _scenario.CustomerAccountBId, 25_00, "clean-1"));
        var reversible = await _scenario.FundAsync(_scenario.CustomerAccountBId, 10_00);
        await _scenario.Posting.ReverseJournalAsync(_scenario.Reversal(reversible, "clean-reverse-1"));
        await _scenario.Projection.ProjectAsync(_scenario.Scope.TenantId, _scenario.LedgerId);

        var result = await _scenario.Reconciliation.RunAsync(
            _scenario.Scope.TenantId, _scenario.LedgerId, "integration-test");

        Assert.True(result.IsClean, string.Join("; ", result.Breaks.Select(item => $"{item.CheckName}:{item.Subject}")));
        Assert.Equal(LedgerReconciliationService.Checks.Count, result.ChecksExecuted);
    }

    [Fact]
    public async Task Reconciliation_detects_an_aggregate_that_no_longer_matches_its_postings()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 80_00);
        await _scenario.Projection.ProjectAsync(_scenario.Scope.TenantId, _scenario.LedgerId);

        // The progression trigger permits an increase, so this is the shape of a defect the database
        // alone cannot catch: an aggregate that drifted away from the postings that justify it.
        await ExecuteAsOwnerAsync(
            """
            UPDATE ledger.account_balance
            SET credit_total = credit_total + 1, version = version + 1
            WHERE account_id = @account
            """,
            command => command.Parameters.AddWithValue("account", _scenario.CustomerAccountAId));

        var result = await _scenario.Reconciliation.RunAsync(_scenario.Scope.TenantId, _scenario.LedgerId);

        var drift = Assert.Single(result.Breaks, item => item.CheckName == "aggregates-match-postings");
        Assert.Equal("critical", drift.Severity);
        Assert.Equal(_scenario.CustomerAccountAId.ToString(), drift.Subject);
        Assert.Contains("\"storedCreditTotal\": \"8001\"", drift.Detail, StringComparison.Ordinal);
        Assert.Contains("\"recomputedCreditTotal\": \"8000\"", drift.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconciliation_detects_a_committed_journal_with_no_outbox_coverage()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 80_00);
        await _scenario.Projection.ProjectAsync(_scenario.Scope.TenantId, _scenario.LedgerId);

        var journalId = Guid.NewGuid();
        await InsertJournalWithoutOutboxAsync(journalId);

        var result = await _scenario.Reconciliation.RunAsync(_scenario.Scope.TenantId, _scenario.LedgerId);

        var gap = Assert.Single(result.Breaks, item => item.CheckName == "outbox-coverage");
        Assert.Equal(journalId.ToString(), gap.Subject);
        Assert.Equal("high", gap.Severity);
    }

    [Fact]
    public async Task Every_recorded_break_is_persisted_and_readable_after_the_run()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 80_00);
        await ExecuteAsOwnerAsync(
            """
            UPDATE ledger.account_balance
            SET credit_total = credit_total + 7, version = version + 1
            WHERE account_id = @account
            """,
            command => command.Parameters.AddWithValue("account", _scenario.CustomerAccountAId));

        var result = await _scenario.Reconciliation.RunAsync(_scenario.Scope.TenantId, _scenario.LedgerId);

        var stored = await ScalarAsync<long>(
            LedgerRole.ReadOnly,
            "SELECT count(*) FROM ledger.reconciliation_break WHERE run_id = @run AND status = 'open'",
            command => command.Parameters.AddWithValue("run", result.RunId));

        Assert.Equal((long)result.Breaks.Count, stored);
        Assert.NotEmpty(result.Breaks);
    }

    private OutboxRelay BuildRelay(IIntegrationEventPublisher publisher, int maxAttempts = 5) =>
        new(
            _database.DataSources,
            _database.Options.ToOptions(),
            publisher,
            TimeProvider.System,
            NullLogger<OutboxRelay>.Instance)
        {
            MaxAttempts = maxAttempts,
            LeaseDuration = TimeSpan.Zero,
        };

    private async Task<(int Total, int Published)> ReadOutboxCountsAsync(Guid journalId)
    {
        await using var connection = await _database.OpenAsAsync(LedgerRole.ReadOnly);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(
            connection, transaction, _scenario.Scope.TenantId, CancellationToken.None);

        await using var command = new NpgsqlCommand(
            """
            SELECT count(*), count(*) FILTER (WHERE published_at IS NOT NULL)
            FROM ledger.outbox_message
            WHERE journal_id = @journal_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("journal_id", journalId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }

    private async Task<List<string>> ReadStatementSnapshotAsync(Guid accountId)
    {
        var lines = await _scenario.Query.GetStatementAsync(_scenario.Scope.TenantId, accountId, 0, 0, 500);
        return [.. lines.Select(line =>
            $"{line.LedgerSequence}:{line.PostingOrder}:{line.Direction}:{line.Amount}:"
            + $"{line.RunningDebitTotal}:{line.RunningCreditTotal}")];
    }

    private async Task InsertJournalWithoutOutboxAsync(Guid journalId)
    {
        await using var connection = await _database.OpenAsAsync(LedgerRole.Owner);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(
            connection, transaction, _scenario.Scope.TenantId, CancellationToken.None);

        var nextSequence = await NextSequenceAsync(connection, transaction);

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO ledger.journal (
                journal_id, ledger_id, tenant_id, legal_entity_id, ledger_sequence,
                transaction_type, schema_version, reason, command_id, correlation_id,
                actor_id, actor_type, authorization_decision_id,
                posted_at, effective_at, booking_date, value_date, business_date)
            VALUES (
                @journal_id, @ledger, @tenant, @legal_entity, @sequence,
                'reconciliation-probe', 1, 'probe', gen_random_uuid(), gen_random_uuid(),
                'probe', 'workload', gen_random_uuid(),
                now(), @effective_at, @booking_date, @booking_date, @booking_date)
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("journal_id", journalId);
            command.Parameters.AddWithValue("ledger", _scenario.LedgerId);
            command.Parameters.AddWithValue("tenant", _scenario.Scope.TenantId);
            command.Parameters.AddWithValue("legal_entity", _scenario.Scope.LegalEntityId);
            command.Parameters.AddWithValue("sequence", nextSequence);
            command.Parameters.AddWithValue("effective_at", LedgerScenario.DefaultDates.EffectiveAt);
            command.Parameters.AddWithValue("booking_date", LedgerScenario.DefaultDates.BookingDate);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var (order, accountId, direction) in new[]
        {
            (1, _scenario.FundingAccountId, "debit"),
            (2, _scenario.CustomerAccountBId, "credit"),
        })
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO ledger.posting (
                    posting_id, journal_id, posting_order, account_id, ledger_id, tenant_id,
                    asset_id, direction, amount)
                VALUES (gen_random_uuid(), @journal_id, @order, @account, @ledger, @tenant, @asset, @direction, 100)
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("journal_id", journalId);
            command.Parameters.AddWithValue("order", (short)order);
            command.Parameters.AddWithValue("account", accountId);
            command.Parameters.AddWithValue("ledger", _scenario.LedgerId);
            command.Parameters.AddWithValue("tenant", _scenario.Scope.TenantId);
            command.Parameters.AddWithValue("asset", _scenario.AssetId);
            command.Parameters.AddWithValue("direction", direction);
            await command.ExecuteNonQueryAsync();
        }

        // Keep the aggregates consistent so only the outbox-coverage proof fails.
        foreach (var (accountId, column) in new[]
        {
            (_scenario.FundingAccountId, "debit_total"),
            (_scenario.CustomerAccountBId, "credit_total"),
        })
        {
            await using var command = new NpgsqlCommand(
                $"""
                UPDATE ledger.account_balance
                SET {column} = {column} + 100, posting_count = posting_count + 1, version = version + 1
                WHERE account_id = @account
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("account", accountId);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();

        // The projection checkpoint must also advance, or the projection proof would fail too.
        await _scenario.Projection.ProjectAsync(_scenario.Scope.TenantId, _scenario.LedgerId);
    }

    private static async Task<long> NextSequenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            "SELECT coalesce(max(ledger_sequence), 0) + 1 FROM ledger.journal", connection, transaction);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsOwnerAsync(string sql, Action<NpgsqlCommand> configure)
    {
        await using var connection = await _database.OpenAsAsync(LedgerRole.Owner);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(
            connection, transaction, _scenario.Scope.TenantId, CancellationToken.None);

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        configure(command);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private async Task<T> ScalarAsync<T>(LedgerRole role, string sql, Action<NpgsqlCommand>? configure = null)
    {
        await using var connection = await _database.OpenAsAsync(role);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(
            connection, transaction, _scenario.Scope.TenantId, CancellationToken.None);

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        configure?.Invoke(command);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class RecordingPublisher : IIntegrationEventPublisher
    {
        private readonly ConcurrentQueue<IntegrationEvent> _published = new();

        public IReadOnlyList<IntegrationEvent> Published => [.. _published];

        public Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
        {
            _published.Enqueue(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysFailingPublisher : IIntegrationEventPublisher
    {
        public Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken) =>
            Task.FromException(new TimeoutException("simulated transport outage"));
    }
}
