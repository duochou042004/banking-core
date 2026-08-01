using BankingCore.Ledger.Model;
using BankingCore.Ledger.Money;

namespace BankingCore.Ledger.UnitTests;

/// <summary>
/// The invariant engine, exercised without a database.
/// </summary>
/// <remarks>
/// These are the named accounting examples the quality gates require for financial behaviour
/// (docs/delivery/quality-gates.md, "Pull request gates"). The same rules are separately proven
/// against a real PostgreSQL instance and against a generated command sequence.
/// </remarks>
public sealed class JournalValidatorTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LegalEntityId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LedgerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid UsdAssetId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid EurAssetId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid AccountA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AccountB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid EurAccount = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    [Fact]
    public void A_balanced_two_leg_journal_is_accepted_and_yields_matching_deltas()
    {
        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 100),
            Leg(2, AccountB, PostingDirection.Credit, 100),
        ]);

        var error = JournalValidator.Validate(draft, Accounts(), isEffectivePeriodOpen: true, out var validated);

        Assert.Null(error);
        Assert.NotNull(validated);
        Assert.Equal(2, validated.Deltas.Count);

        var debited = validated.Deltas.Single(delta => delta.AccountId == AccountA);
        var credited = validated.Deltas.Single(delta => delta.AccountId == AccountB);
        Assert.Equal("100", debited.DebitDelta.ToString());
        Assert.Equal("0", debited.CreditDelta.ToString());
        Assert.Equal("100", credited.CreditDelta.ToString());
        Assert.Equal("0", credited.DebitDelta.ToString());
    }

    [Fact]
    public void A_balanced_multi_leg_journal_is_accepted()
    {
        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 60),
            Leg(2, AccountA, PostingDirection.Debit, 40),
            Leg(3, AccountB, PostingDirection.Credit, 100),
        ]);

        var error = JournalValidator.Validate(draft, Accounts(), isEffectivePeriodOpen: true, out var validated);

        Assert.Null(error);
        var debited = validated!.Deltas.Single(delta => delta.AccountId == AccountA);
        Assert.Equal("100", debited.DebitDelta.ToString());
        Assert.Equal(2, debited.PostingCount);
    }

    [Fact]
    public void A_single_leg_journal_is_rejected()
    {
        var draft = Draft([Leg(1, AccountA, PostingDirection.Debit, 100)]);

        var error = JournalValidator.Validate(draft, Accounts(), isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.JournalTooFewPostings, error!.Code);
    }

    [Fact]
    public void A_zero_amount_posting_is_rejected()
    {
        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 0),
            Leg(2, AccountB, PostingDirection.Credit, 0),
        ]);

        var error = JournalValidator.Validate(draft, Accounts(), isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.ZeroPostingAmount, error!.Code);
    }

    [Fact]
    public void An_unbalanced_journal_is_rejected()
    {
        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 100),
            Leg(2, AccountB, PostingDirection.Credit, 99),
        ]);

        var error = JournalValidator.Validate(draft, Accounts(), isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.JournalNotBalanced, error!.Code);
    }

    [Fact]
    public void A_cross_currency_journal_cannot_be_balanced_by_a_converted_number()
    {
        // AG-006: USD 100 debited against EUR 92 credited. Even at a correct market rate this is not
        // a balanced journal; an exchange needs separately balanced legs per asset.
        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 100),
            Leg(2, EurAccount, PostingDirection.Credit, 92),
        ]);

        var error = JournalValidator.Validate(draft, Accounts(), isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.JournalNotBalanced, error!.Code);
    }

    [Fact]
    public void A_journal_that_balances_within_each_asset_is_accepted()
    {
        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 100),
            Leg(2, AccountB, PostingDirection.Credit, 100),
            Leg(3, EurAccount, PostingDirection.Debit, 92),
            Leg(4, EurAccount, PostingDirection.Credit, 92),
        ]);

        var error = JournalValidator.Validate(draft, Accounts(), isEffectivePeriodOpen: true, out _);

        Assert.Null(error);
    }

    [Fact]
    public void A_repeated_posting_order_is_rejected()
    {
        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 100),
            Leg(1, AccountB, PostingDirection.Credit, 100),
        ]);

        var error = JournalValidator.Validate(draft, Accounts(), isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.DuplicatePostingOrder, error!.Code);
    }

    [Fact]
    public void An_unknown_account_is_rejected()
    {
        var draft = Draft([
            Leg(1, Guid.NewGuid(), PostingDirection.Debit, 100),
            Leg(2, AccountB, PostingDirection.Credit, 100),
        ]);

        var error = JournalValidator.Validate(draft, Accounts(), isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.UnknownAccount, error!.Code);
    }

    [Fact]
    public void An_account_belonging_to_another_tenant_is_reported_as_unknown_not_as_forbidden()
    {
        // AG-011: the rejection must not confirm that the identifier exists somewhere else.
        var foreignScope = new LedgerScope(Guid.NewGuid(), Guid.NewGuid());
        var accounts = Accounts();
        accounts[AccountB] = accounts[AccountB] with
        {
            Account = accounts[AccountB].Account with { Scope = foreignScope },
        };

        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 100),
            Leg(2, AccountB, PostingDirection.Credit, 100),
        ]);

        var error = JournalValidator.Validate(draft, accounts, isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.UnknownAccount, error!.Code);
    }

    [Fact]
    public void An_account_in_another_ledger_is_rejected()
    {
        var accounts = Accounts();
        accounts[AccountB] = accounts[AccountB] with
        {
            Account = accounts[AccountB].Account with { LedgerId = Guid.NewGuid() },
        };

        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 100),
            Leg(2, AccountB, PostingDirection.Credit, 100),
        ]);

        var error = JournalValidator.Validate(draft, accounts, isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.AccountLedgerMismatch, error!.Code);
    }

    [Theory]
    [InlineData(AccountStatus.Frozen)]
    [InlineData(AccountStatus.Closed)]
    public void An_account_that_is_not_open_is_rejected(AccountStatus status)
    {
        var accounts = Accounts();
        accounts[AccountB] = accounts[AccountB] with
        {
            Account = accounts[AccountB].Account with { Status = status },
        };

        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 100),
            Leg(2, AccountB, PostingDirection.Credit, 100),
        ]);

        var error = JournalValidator.Validate(draft, accounts, isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.AccountNotOpen, error!.Code);
    }

    [Fact]
    public void An_inactive_asset_is_rejected()
    {
        var accounts = Accounts();
        accounts[AccountB] = accounts[AccountB] with
        {
            Asset = accounts[AccountB].Asset with { Status = AssetStatus.Suspended },
        };

        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 100),
            Leg(2, AccountB, PostingDirection.Credit, 100),
        ]);

        var error = JournalValidator.Validate(draft, accounts, isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.AssetNotActive, error!.Code);
    }

    [Fact]
    public void A_closed_effective_period_is_rejected()
    {
        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 100),
            Leg(2, AccountB, PostingDirection.Credit, 100),
        ]);

        var error = JournalValidator.Validate(draft, Accounts(), isEffectivePeriodOpen: false, out _);

        Assert.Equal(LedgerErrorCode.AccountingPeriodClosed, error!.Code);
    }

    [Fact]
    public void The_balance_policy_is_applied_to_the_resulting_aggregates_not_the_prior_ones()
    {
        // AccountA is debit-normal with 100 debited and 100 credited, so its balance is exactly zero.
        // Debiting it further is fine; crediting it further would drive it negative.
        var accounts = Accounts(
            accountABalance: new AccountBalance(AccountA, UsdAssetId, Amount.FromCoefficient(100), Amount.FromCoefficient(100), 2, 2));

        var withdrawal = Draft([
            Leg(1, AccountB, PostingDirection.Debit, 1),
            Leg(2, AccountA, PostingDirection.Credit, 1),
        ]);
        var deposit = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 1),
            Leg(2, AccountB, PostingDirection.Credit, 1),
        ]);

        Assert.Equal(
            LedgerErrorCode.BalancePolicyViolation,
            JournalValidator.Validate(withdrawal, accounts, isEffectivePeriodOpen: true, out _)!.Code);
        Assert.Null(JournalValidator.Validate(deposit, accounts, isEffectivePeriodOpen: true, out _));
    }

    [Fact]
    public void An_account_with_an_unrestricted_policy_may_go_negative()
    {
        var accounts = Accounts();
        accounts[AccountA] = accounts[AccountA] with
        {
            Account = accounts[AccountA].Account with { BalancePolicy = BalancePolicy.Unrestricted },
        };

        var draft = Draft([
            Leg(1, AccountB, PostingDirection.Debit, 100),
            Leg(2, AccountA, PostingDirection.Credit, 100),
        ]);

        Assert.Null(JournalValidator.Validate(draft, accounts, isEffectivePeriodOpen: true, out _));
    }

    [Fact]
    public void An_aggregate_that_would_leave_the_supported_range_is_rejected()
    {
        var accounts = Accounts(
            accountABalance: new AccountBalance(
                AccountA, UsdAssetId, Amount.FromCoefficient(Amount.MaxCoefficient), Amount.Zero, 1, 1));

        var draft = Draft([
            Leg(1, AccountA, PostingDirection.Debit, 1),
            Leg(2, AccountB, PostingDirection.Credit, 1),
        ]);

        var error = JournalValidator.Validate(draft, accounts, isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.AmountOutOfRange, error!.Code);
    }

    [Fact]
    public void A_reversal_mirrors_every_leg_and_links_back_to_the_original()
    {
        var original = new PostedJournal(
            Guid.NewGuid(),
            LedgerId,
            new LedgerScope(TenantId, LegalEntityId),
            7,
            "internal-transfer",
            "original",
            Dates,
            DateTimeOffset.UtcNow,
            null,
            null,
            [
                new PostedPosting(Guid.NewGuid(), 1, AccountA, UsdAssetId, PostingDirection.Debit, Amount.FromCoefficient(100)),
                new PostedPosting(Guid.NewGuid(), 2, AccountB, UsdAssetId, PostingDirection.Credit, Amount.FromCoefficient(100)),
            ]);

        var reversal = JournalValidator.BuildReversal(original, Dates, Authority, "correction", Guid.NewGuid());

        Assert.Equal(original.JournalId, reversal.ReversesJournalId);
        Assert.Equal(original.JournalId, reversal.CausationId);
        Assert.Equal("internal-transfer.reversal", reversal.TransactionType);
        Assert.Equal(PostingDirection.Credit, reversal.Postings[0].Direction);
        Assert.Equal(AccountA, reversal.Postings[0].AccountId);
        Assert.Equal(PostingDirection.Debit, reversal.Postings[1].Direction);
        Assert.Equal(AccountB, reversal.Postings[1].AccountId);
        Assert.Equal("100", reversal.Postings[0].Amount.ToString());

        // The reversal is itself a balanced journal, not an edit of the original. It is validated
        // against the state the original left behind, so AccountA already carries the 100 debit.
        var afterOriginal = Accounts(
            accountABalance: new AccountBalance(
                AccountA, UsdAssetId, Amount.FromCoefficient(100), Amount.Zero, 1, 1));

        Assert.Null(JournalValidator.Validate(reversal, afterOriginal, isEffectivePeriodOpen: true, out _));
    }

    [Fact]
    public void A_reversal_is_subject_to_the_same_balance_policy_as_any_other_journal()
    {
        // A consequence worth stating plainly: if the value has already moved on, reversing is
        // rejected rather than silently driving a restricted account negative. The correct remedy is
        // an authorized adjustment, not a weaker rule (docs/architecture/ledger.md, "Prohibited designs").
        var original = new PostedJournal(
            Guid.NewGuid(),
            LedgerId,
            new LedgerScope(TenantId, LegalEntityId),
            7,
            "internal-transfer",
            "original",
            Dates,
            DateTimeOffset.UtcNow,
            null,
            null,
            [
                new PostedPosting(Guid.NewGuid(), 1, AccountA, UsdAssetId, PostingDirection.Debit, Amount.FromCoefficient(100)),
                new PostedPosting(Guid.NewGuid(), 2, AccountB, UsdAssetId, PostingDirection.Credit, Amount.FromCoefficient(100)),
            ]);

        var reversal = JournalValidator.BuildReversal(original, Dates, Authority, "correction", Guid.NewGuid());

        // AccountA's debit has since been spent back down to zero, so crediting it would go negative.
        var spent = Accounts(
            accountABalance: new AccountBalance(
                AccountA, UsdAssetId, Amount.FromCoefficient(100), Amount.FromCoefficient(100), 2, 2));

        var error = JournalValidator.Validate(reversal, spent, isEffectivePeriodOpen: true, out _);

        Assert.Equal(LedgerErrorCode.BalancePolicyViolation, error!.Code);
    }

    private static readonly JournalDates Dates = new(
        new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero),
        new DateOnly(2026, 6, 15),
        new DateOnly(2026, 6, 15),
        new DateOnly(2026, 6, 15));

    private static readonly CommandAuthority Authority =
        new("workload:unit-tests", ActorType.Workload, Guid.Parse("66666666-6666-6666-6666-666666666666"));

    private static JournalDraft Draft(PostingDraft[] postings) => new(
        LedgerId,
        new LedgerScope(TenantId, LegalEntityId),
        "internal-transfer",
        1,
        "unit test",
        null,
        Dates,
        Authority,
        Guid.Parse("77777777-7777-7777-7777-777777777777"),
        null,
        postings,
        null);

    private static PostingDraft Leg(int order, Guid accountId, PostingDirection direction, long amount) =>
        new(order, accountId, direction, Amount.FromCoefficient(amount));

    private static Dictionary<Guid, AccountPostingContext> Accounts(AccountBalance? accountABalance = null)
    {
        var usd = new Asset(UsdAssetId, "USD", AssetScale.FromInt32(2), AssetStatus.Active, "iso-4217", "USD");
        var eur = new Asset(EurAssetId, "EUR", AssetScale.FromInt32(2), AssetStatus.Active, "iso-4217", "EUR");
        var scope = new LedgerScope(TenantId, LegalEntityId);

        return new Dictionary<Guid, AccountPostingContext>
        {
            [AccountA] = new(
                new LedgerAccount(
                    AccountA, LedgerId, scope, "a", UsdAssetId, AccountClass.Asset,
                    PostingDirection.Debit, AccountStatus.Open, BalancePolicy.NeverNegative),
                usd,
                accountABalance ?? new AccountBalance(AccountA, UsdAssetId, Amount.Zero, Amount.Zero, 0, 0)),
            [AccountB] = new(
                new LedgerAccount(
                    AccountB, LedgerId, scope, "b", UsdAssetId, AccountClass.Equity,
                    PostingDirection.Credit, AccountStatus.Open, BalancePolicy.Unrestricted),
                usd,
                new AccountBalance(AccountB, UsdAssetId, Amount.Zero, Amount.Zero, 0, 0)),
            [EurAccount] = new(
                new LedgerAccount(
                    EurAccount, LedgerId, scope, "eur", EurAssetId, AccountClass.Equity,
                    PostingDirection.Credit, AccountStatus.Open, BalancePolicy.Unrestricted),
                eur,
                new AccountBalance(EurAccount, EurAssetId, Amount.Zero, Amount.Zero, 0, 0)),
        };
    }
}
