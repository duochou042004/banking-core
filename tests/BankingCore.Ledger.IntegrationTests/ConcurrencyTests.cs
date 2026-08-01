using BankingCore.Ledger.IntegrationTests.Infrastructure;
using BankingCore.Ledger.Persistence;
using Npgsql;

namespace BankingCore.Ledger.IntegrationTests;

/// <summary>
/// Behaviour of the posting path under simultaneous commands.
/// </summary>
/// <remarks>
/// Competing operations are released from a shared barrier and repeated, and the final state is
/// validated by recomputation from the immutable postings rather than by reading the cached
/// aggregates the code under test wrote (docs/delivery/testing-strategy.md, "Concurrency and
/// linearization"). Passing these tests does not prove all schedules; they are one proof alongside
/// serializable isolation, database constraints, and reconciliation (evaluation AG-015).
/// </remarks>
public sealed class ConcurrencyTests : IAsyncLifetime
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
    public async Task Simultaneous_withdrawals_cannot_overspend_a_never_negative_account()
    {
        // Fund exactly three withdrawals, then attempt ten at once.
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 30_00);

        const int attempts = 10;
        const long withdrawal = 10_00;

        var results = await RunSimultaneouslyAsync(attempts, index =>
            PostWithClientRetryAsync(_scenario.Transfer(
                _scenario.CustomerAccountAId,
                _scenario.CustomerAccountBId,
                withdrawal,
                $"withdraw-{index}")));

        var posted = results.Count(result => result.Kind == PostingOutcomeKind.Posted);
        var rejected = results
            .Where(result => result.Kind == PostingOutcomeKind.Rejected)
            .ToArray();

        Assert.Equal(3, posted);
        Assert.Equal(attempts - 3, rejected.Length);
        Assert.All(rejected, result => Assert.Equal(
            LedgerErrorCode.BalancePolicyViolation, result.Error!.Code));

        Assert.Equal(Int128.Zero, await _scenario.PostedBalanceAsync(_scenario.CustomerAccountAId));
        Assert.Equal((Int128)30_00, await _scenario.PostedBalanceAsync(_scenario.CustomerAccountBId));
        await AssertAggregatesMatchPostingsAsync();
    }

    [Fact]
    public async Task Simultaneous_identical_commands_commit_exactly_one_journal()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 100_00);
        var command = _scenario.Transfer(
            _scenario.CustomerAccountAId, _scenario.CustomerAccountBId, 25_00, "race-key");

        var results = await RunSimultaneouslyAsync(
            8, _ => _scenario.Posting.PostInternalTransferAsync(command));

        var posted = results.Where(result => result.Kind == PostingOutcomeKind.Posted).ToArray();
        var replayed = results.Where(result => result.Kind == PostingOutcomeKind.IdempotentReplay).ToArray();
        var transient = results.Where(result => result.Kind == PostingOutcomeKind.Rejected).ToArray();

        Assert.Single(posted);
        Assert.All(transient, result => Assert.Equal(
            LedgerErrorCode.ConcurrencyRetryExhausted, result.Error!.Code));
        Assert.All(replayed, result => Assert.Equal(posted[0].JournalId, result.JournalId));

        // Whatever each caller was told, exactly one journal exists and the value moved once.
        Assert.Equal(1, await CountJournalsForKeyAsync("race-key"));
        Assert.Equal((Int128)25_00, await _scenario.PostedBalanceAsync(_scenario.CustomerAccountBId));
        await AssertAggregatesMatchPostingsAsync();
    }

    [Fact]
    public async Task Simultaneous_distinct_transfers_produce_a_dense_sequence_with_no_duplicates()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 1_000_00);

        const int transfers = 20;
        var results = await RunSimultaneouslyAsync(transfers, index =>
            PostWithClientRetryAsync(_scenario.Transfer(
                _scenario.CustomerAccountAId,
                _scenario.CustomerAccountBId,
                1_00,
                $"dense-{index}")));

        Assert.All(results, result => Assert.Equal(PostingOutcomeKind.Posted, result.Kind));

        var sequences = results.Select(result => result.LedgerSequence!.Value).Order().ToArray();
        // The funding journal took sequence 1, so these occupy 2..21 with no gap or repeat.
        Assert.Equal(Enumerable.Range(2, transfers).Select(value => (long)value), sequences);

        Assert.Equal((Int128)(1_000_00 - (transfers * 1_00)), await _scenario.PostedBalanceAsync(_scenario.CustomerAccountAId));
        await AssertAggregatesMatchPostingsAsync();
    }

    [Fact]
    public async Task A_hot_account_stays_consistent_under_bidirectional_contention()
    {
        await _scenario.FundAsync(_scenario.CustomerAccountAId, 500_00);
        await _scenario.FundAsync(_scenario.CustomerAccountBId, 500_00);

        const int pairs = 12;
        var results = await RunSimultaneouslyAsync(pairs * 2, index =>
        {
            var forward = index % 2 == 0;
            return PostWithClientRetryAsync(_scenario.Transfer(
                forward ? _scenario.CustomerAccountAId : _scenario.CustomerAccountBId,
                forward ? _scenario.CustomerAccountBId : _scenario.CustomerAccountAId,
                5_00,
                $"hot-{index}"));
        });

        Assert.All(results, result => Assert.Equal(PostingOutcomeKind.Posted, result.Kind));

        // Equal traffic in both directions leaves both balances where they started.
        Assert.Equal((Int128)500_00, await _scenario.PostedBalanceAsync(_scenario.CustomerAccountAId));
        Assert.Equal((Int128)500_00, await _scenario.PostedBalanceAsync(_scenario.CustomerAccountBId));
        await AssertAggregatesMatchPostingsAsync();

        var reconciliation = await _scenario.Reconciliation.RunAsync(_scenario.Scope.TenantId, _scenario.LedgerId);
        Assert.False(
            reconciliation.Breaks.Any(item => item.CheckName != "statement-projection-matches-postings"),
            string.Join("; ", reconciliation.Breaks.Select(item => $"{item.CheckName}:{item.Subject}")));
    }

    /// <summary>
    /// Posts a command the way the contract tells a client to: a retryable outcome is retried with
    /// the same idempotency key, so the ledger's bounded server-side retry budget and the client's
    /// retry compose into progress without any risk of a second journal.
    /// </summary>
    private async Task<PostingResult> PostWithClientRetryAsync(
        BankingCore.Ledger.Commands.InternalTransferCommand command,
        int maxClientAttempts = 6)
    {
        PostingResult result;
        var attempt = 0;
        do
        {
            attempt++;
            result = await _scenario.Posting.PostInternalTransferAsync(command);
            if (result.Error?.IsRetryable != true)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt));
        }
        while (attempt < maxClientAttempts);

        return result;
    }

    private static async Task<IReadOnlyList<PostingResult>> RunSimultaneouslyAsync(
        int count,
        Func<int, Task<PostingResult>> operation)
    {
        using var barrier = new SemaphoreSlim(0, count);
        var tasks = new Task<PostingResult>[count];

        for (var index = 0; index < count; index++)
        {
            var captured = index;
            tasks[index] = Task.Run(async () =>
            {
                await barrier.WaitAsync();
                return await operation(captured);
            });
        }

        barrier.Release(count);
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Recomputes every aggregate from the immutable postings and compares it with the stored value.
    /// This is the linearization check: whatever schedule the database chose, the cached aggregates
    /// must equal the sum of what was actually posted.
    /// </summary>
    private async Task AssertAggregatesMatchPostingsAsync()
    {
        await using var connection = await _database.OpenAsAsync(LedgerRole.ReadOnly);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(
            connection, transaction, _scenario.Scope.TenantId, CancellationToken.None);

        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM ledger.account_balance b
            LEFT JOIN (
                SELECT account_id,
                       coalesce(sum(amount) FILTER (WHERE direction = 'debit'), 0) AS debit_total,
                       coalesce(sum(amount) FILTER (WHERE direction = 'credit'), 0) AS credit_total,
                       count(*) AS posting_count
                FROM ledger.posting
                GROUP BY account_id
            ) r ON r.account_id = b.account_id
            WHERE b.debit_total <> coalesce(r.debit_total, 0)
               OR b.credit_total <> coalesce(r.credit_total, 0)
               OR b.posting_count <> coalesce(r.posting_count, 0)
            """,
            connection,
            transaction);

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    private async Task<int> CountJournalsForKeyAsync(string idempotencyKey)
    {
        await using var connection = await _database.OpenAsAsync(LedgerRole.ReadOnly);
        await using var transaction = await connection.BeginTransactionAsync();
        await LedgerUnitOfWork.BindTenantAsync(
            connection, transaction, _scenario.Scope.TenantId, CancellationToken.None);

        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM ledger.journal j
            JOIN ledger.idempotency_receipt r ON r.outcome_journal_id = j.journal_id
            WHERE r.idempotency_key = @key
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("key", idempotencyKey);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }
}
