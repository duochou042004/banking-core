using BankingCore.Ledger;
using BankingCore.Ledger.IntegrationTests.Infrastructure;
using BankingCore.Ledger.Model;
using BankingCore.Ledger.Persistence;

namespace BankingCore.Ledger.IntegrationTests;

/// <summary>
/// The accounting behaviour of the posting path against a real PostgreSQL instance.
/// </summary>
public sealed class PostingPathTests : IAsyncLifetime
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
    public void Migrations_apply_in_order_and_are_all_recorded()
    {
        Assert.Equal(
            [
                "0001_schemas_roles_and_extensions",
                "0002_reference_and_accounts",
                "0003_journals_and_postings",
                "0004_idempotency_outbox_and_audit",
                "0005_projection_and_reconciliation",
                "0006_row_level_security_and_grants",
            ],
            _database.AppliedMigrations);
    }

    [Fact]
    public async Task A_balanced_transfer_posts_and_moves_exactly_the_requested_amount()
    {
        var result = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.FundingAccountId, _scenario.CustomerAccountAId, 125_00, "transfer-1"));

        Assert.Equal(PostingOutcomeKind.Posted, result.Kind);
        Assert.NotNull(result.JournalId);
        Assert.Equal(1, result.LedgerSequence);

        // Funding is a credit-normal equity account debited by the transfer, so its posted balance
        // moves negative; the customer's credit-normal liability balance moves positive by the
        // identical amount. Value is conserved.
        Assert.Equal((Int128)(-125_00), await _scenario.PostedBalanceAsync(_scenario.FundingAccountId));
        Assert.Equal((Int128)125_00, await _scenario.PostedBalanceAsync(_scenario.CustomerAccountAId));
    }

    [Fact]
    public async Task Ledger_sequence_is_dense_and_strictly_increasing()
    {
        var first = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.FundingAccountId, _scenario.CustomerAccountAId, 100, "seq-1"));
        var second = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.FundingAccountId, _scenario.CustomerAccountBId, 200, "seq-2"));
        var third = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.CustomerAccountAId, _scenario.CustomerAccountBId, 50, "seq-3"));

        Assert.Equal([1L, 2L, 3L], new[] { first, second, third }.Select(r => r.LedgerSequence!.Value));
    }

    [Fact]
    public async Task An_identical_retry_returns_the_original_outcome_without_posting_again()
    {
        var command = _scenario.Transfer(
            _scenario.FundingAccountId, _scenario.CustomerAccountAId, 10_00, "idempotent-1");

        var first = await _scenario.Posting.PostInternalTransferAsync(command);
        var replay = await _scenario.Posting.PostInternalTransferAsync(command);

        Assert.Equal(PostingOutcomeKind.Posted, first.Kind);
        Assert.Equal(PostingOutcomeKind.IdempotentReplay, replay.Kind);
        Assert.Equal(first.JournalId, replay.JournalId);
        Assert.Equal(first.LedgerSequence, replay.LedgerSequence);
        Assert.Equal((Int128)10_00, await _scenario.PostedBalanceAsync(_scenario.CustomerAccountAId));
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_amount_is_a_conflict_and_preserves_the_original()
    {
        await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.FundingAccountId, _scenario.CustomerAccountAId, 10_00, "conflict-1"));

        var conflicting = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.FundingAccountId, _scenario.CustomerAccountAId, 20_00, "conflict-1"));

        Assert.Equal(PostingOutcomeKind.Rejected, conflicting.Kind);
        Assert.Equal(LedgerErrorCode.IdempotencyConflict, conflicting.Error!.Code);
        Assert.Equal((Int128)10_00, await _scenario.PostedBalanceAsync(_scenario.CustomerAccountAId));
    }

    [Fact]
    public async Task A_deterministic_rejection_is_stored_and_returned_again_on_retry()
    {
        var command = _scenario.Transfer(
            _scenario.CustomerAccountAId, _scenario.CustomerAccountBId, 5_00, "insufficient-1");

        var first = await _scenario.Posting.PostInternalTransferAsync(command);
        var retry = await _scenario.Posting.PostInternalTransferAsync(command);

        Assert.Equal(PostingOutcomeKind.Rejected, first.Kind);
        Assert.Equal(LedgerErrorCode.BalancePolicyViolation, first.Error!.Code);
        Assert.Equal(PostingOutcomeKind.Rejected, retry.Kind);
        Assert.Equal(LedgerErrorCode.BalancePolicyViolation, retry.Error!.Code);
        Assert.Equal(Int128.Zero, await _scenario.PostedBalanceAsync(_scenario.CustomerAccountAId));
    }

    [Fact]
    public async Task A_reversal_creates_a_new_linked_journal_and_leaves_the_original_posted()
    {
        var original = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.FundingAccountId, _scenario.CustomerAccountAId, 42_00, "reversible-1"));

        var reversal = await _scenario.Posting.ReverseJournalAsync(
            _scenario.Reversal(original.JournalId!.Value, "reverse-1"));

        Assert.Equal(PostingOutcomeKind.Posted, reversal.Kind);

        var originalJournal = await _scenario.Query.GetJournalAsync(
            _scenario.Scope.TenantId, original.JournalId!.Value);
        var reversingJournal = await _scenario.Query.GetJournalAsync(
            _scenario.Scope.TenantId, reversal.JournalId!.Value);

        Assert.NotNull(originalJournal);
        Assert.NotNull(reversingJournal);
        Assert.Equal(reversal.JournalId, originalJournal.ReversedByJournalId);
        Assert.Equal(original.JournalId, reversingJournal.ReversesJournalId);
        Assert.Equal("internal-transfer.reversal", reversingJournal.TransactionType);

        // Each leg is mirrored, so the net effect on every account is zero.
        Assert.Equal(Int128.Zero, await _scenario.PostedBalanceAsync(_scenario.CustomerAccountAId));
        Assert.Equal(Int128.Zero, await _scenario.PostedBalanceAsync(_scenario.FundingAccountId));

        // Both the original and its reversal remain posted; nothing was edited away.
        Assert.Equal(2, reversingJournal.Postings.Count);
        Assert.Equal(2, originalJournal.Postings.Count);
    }

    [Fact]
    public async Task A_journal_cannot_be_reversed_twice()
    {
        var original = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.FundingAccountId, _scenario.CustomerAccountAId, 7_00, "double-reverse-1"));

        var first = await _scenario.Posting.ReverseJournalAsync(
            _scenario.Reversal(original.JournalId!.Value, "double-reverse-a"));
        var second = await _scenario.Posting.ReverseJournalAsync(
            _scenario.Reversal(original.JournalId!.Value, "double-reverse-b"));

        Assert.Equal(PostingOutcomeKind.Posted, first.Kind);
        Assert.Equal(PostingOutcomeKind.Rejected, second.Kind);
        Assert.Equal(LedgerErrorCode.JournalAlreadyReversed, second.Error!.Code);
    }

    [Fact]
    public async Task A_reversal_cannot_itself_be_reversed()
    {
        var original = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.FundingAccountId, _scenario.CustomerAccountAId, 3_00, "reverse-chain-1"));
        var reversal = await _scenario.Posting.ReverseJournalAsync(
            _scenario.Reversal(original.JournalId!.Value, "reverse-chain-a"));

        var attempted = await _scenario.Posting.ReverseJournalAsync(
            _scenario.Reversal(reversal.JournalId!.Value, "reverse-chain-b"));

        Assert.Equal(PostingOutcomeKind.Rejected, attempted.Kind);
        Assert.Equal(LedgerErrorCode.CannotReverseAReversal, attempted.Error!.Code);
    }

    [Fact]
    public async Task Posting_into_a_closed_period_is_rejected()
    {
        var periodId = await OpenAndCloseJulyAsync();
        Assert.NotEqual(Guid.Empty, periodId);

        var result = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(
                _scenario.FundingAccountId,
                _scenario.CustomerAccountAId,
                1_00,
                "closed-period-1",
                dates: new JournalDates(
                    new DateTimeOffset(2027, 7, 15, 12, 0, 0, TimeSpan.Zero),
                    new DateOnly(2027, 7, 15),
                    new DateOnly(2027, 7, 15),
                    new DateOnly(2027, 7, 15))));

        Assert.Equal(PostingOutcomeKind.Rejected, result.Kind);
        Assert.Equal(LedgerErrorCode.AccountingPeriodClosed, result.Error!.Code);
    }

    [Fact]
    public async Task Posting_outside_any_defined_period_is_rejected()
    {
        var result = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(
                _scenario.FundingAccountId,
                _scenario.CustomerAccountAId,
                1_00,
                "no-period-1",
                dates: new JournalDates(
                    new DateTimeOffset(2030, 3, 1, 12, 0, 0, TimeSpan.Zero),
                    new DateOnly(2030, 3, 1),
                    new DateOnly(2030, 3, 1),
                    new DateOnly(2030, 3, 1))));

        Assert.Equal(PostingOutcomeKind.Rejected, result.Kind);
        Assert.Equal(LedgerErrorCode.AccountingPeriodClosed, result.Error!.Code);
    }

    [Fact]
    public async Task A_transfer_to_an_account_in_another_tenant_is_rejected_as_unknown()
    {
        var otherScenario = await LedgerScenario.CreateAsync(_database);

        var result = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(
                _scenario.FundingAccountId, otherScenario.CustomerAccountAId, 1_00, "cross-tenant-1"));

        Assert.Equal(PostingOutcomeKind.Rejected, result.Kind);
        Assert.Equal(LedgerErrorCode.UnknownAccount, result.Error!.Code);
        Assert.Equal(Int128.Zero, await otherScenario.PostedBalanceAsync(otherScenario.CustomerAccountAId));
    }

    [Fact]
    public async Task A_transfer_to_a_frozen_account_is_rejected()
    {
        var frozen = await _scenario.OpenAccountAsync(
            "frozen-account", AccountClass.Liability, PostingDirection.Credit, BalancePolicy.NeverNegative);

        await using (var connection = await _database.OpenAsAsync(LedgerRole.Admin))
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await LedgerUnitOfWork.BindTenantAsync(
                connection, transaction, _scenario.Scope.TenantId, CancellationToken.None);
            await using var command = new Npgsql.NpgsqlCommand(
                "UPDATE ledger.ledger_account SET status = 'frozen' WHERE account_id = @id",
                connection,
                transaction);
            command.Parameters.AddWithValue("id", frozen);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        var result = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.FundingAccountId, frozen, 1_00, "frozen-1"));

        Assert.Equal(PostingOutcomeKind.Rejected, result.Kind);
        Assert.Equal(LedgerErrorCode.AccountNotOpen, result.Error!.Code);
    }

    [Fact]
    public async Task A_transfer_to_the_same_account_is_rejected_before_any_state_is_read()
    {
        var result = await _scenario.Posting.PostInternalTransferAsync(
            _scenario.Transfer(_scenario.CustomerAccountAId, _scenario.CustomerAccountAId, 1_00, "self-1"));

        Assert.Equal(PostingOutcomeKind.Rejected, result.Kind);
        Assert.Equal(LedgerErrorCode.MalformedRequest, result.Error!.Code);
    }

    private async Task<Guid> OpenAndCloseJulyAsync()
    {
        var periodId = await _scenario.Administration.OpenPeriodAsync(
            new OpenPeriodRequest(
                _scenario.Scope, _scenario.LedgerId, new DateOnly(2027, 7, 1), new DateOnly(2027, 7, 31)),
            _scenario.Authority);
        await _scenario.Administration.ClosePeriodAsync(_scenario.Scope.TenantId, periodId, _scenario.Authority);
        return periodId;
    }
}
