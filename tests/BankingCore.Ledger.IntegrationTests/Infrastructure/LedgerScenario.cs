using BankingCore.Ledger.Commands;
using BankingCore.Ledger.Idempotency;
using BankingCore.Ledger.Model;
using BankingCore.Ledger.Money;
using BankingCore.Ledger.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace BankingCore.Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// A seeded tenant with one ledger, one asset, an open accounting period, and a set of accounts.
/// </summary>
/// <remarks>
/// Every identifier and label is generated; no production or personal data is used
/// (docs/delivery/testing-strategy.md, "Test data").
/// </remarks>
public sealed class LedgerScenario
{
    private static int _sequence;

    private LedgerScenario(LedgerTestDatabase database, TimeProvider timeProvider)
    {
        Database = database;
        TimeProvider = timeProvider;
        Posting = new LedgerPostingService(
            database.DataSources, database.Options.ToOptions(), timeProvider, NullLogger<LedgerPostingService>.Instance);
        Administration = new LedgerAdministrationService(
            database.DataSources,
            database.Options.ToOptions(),
            timeProvider,
            NullLogger<LedgerAdministrationService>.Instance);
        Query = new LedgerQueryService(
            database.DataSources, database.Options.ToOptions(), timeProvider, NullLogger<LedgerQueryService>.Instance);
        Projection = new StatementProjectionBuilder(
            database.DataSources,
            database.Options.ToOptions(),
            timeProvider,
            NullLogger<StatementProjectionBuilder>.Instance);
        Reconciliation = new LedgerReconciliationService(
            database.DataSources,
            database.Options.ToOptions(),
            timeProvider,
            NullLogger<LedgerReconciliationService>.Instance);
        Inbox = new InboxDeduplicator(
            database.DataSources, database.Options.ToOptions(), NullLogger<InboxDeduplicator>.Instance);
    }

    /// <summary>The database the scenario was seeded into.</summary>
    public LedgerTestDatabase Database { get; }

    /// <summary>The injectable clock. Tests control time rather than sleeping.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Posting path under test.</summary>
    public LedgerPostingService Posting { get; }

    /// <summary>Administration path under test.</summary>
    public LedgerAdministrationService Administration { get; }

    /// <summary>Query path under test.</summary>
    public LedgerQueryService Query { get; }

    /// <summary>Statement projection under test.</summary>
    public StatementProjectionBuilder Projection { get; }

    /// <summary>Reconciliation under test.</summary>
    public LedgerReconciliationService Reconciliation { get; }

    /// <summary>Consumer-side deduplication under test.</summary>
    public InboxDeduplicator Inbox { get; }

    /// <summary>The seeded scope.</summary>
    public LedgerScope Scope { get; private set; }

    /// <summary>The seeded ledger.</summary>
    public Guid LedgerId { get; private set; }

    /// <summary>The seeded asset, scale 2, like a minor-unit currency.</summary>
    public Guid AssetId { get; private set; }

    /// <summary>Funding account. Liability side, may go negative, used to originate value.</summary>
    public Guid FundingAccountId { get; private set; }

    /// <summary>Customer account A. Liability side, may not go negative.</summary>
    public Guid CustomerAccountAId { get; private set; }

    /// <summary>Customer account B. Liability side, may not go negative.</summary>
    public Guid CustomerAccountBId { get; private set; }

    /// <summary>The authority every seeded command runs under.</summary>
    public CommandAuthority Authority { get; } =
        new("workload:integration-tests", ActorType.Workload, Guid.NewGuid());

    /// <summary>Seeds a fresh tenant, ledger, asset, period, and accounts.</summary>
    public static async Task<LedgerScenario> CreateAsync(
        LedgerTestDatabase database,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        var scenario = new LedgerScenario(database, timeProvider ?? TimeProvider.System);
        var ordinal = Interlocked.Increment(ref _sequence);

        scenario.Scope = new LedgerScope(Guid.NewGuid(), Guid.NewGuid());

        scenario.AssetId = await scenario.Administration.DefineAssetAsync(
            new DefineAssetRequest($"TST{ordinal:D4}", AssetScale.FromInt32(2), "iso-4217", "XTS"),
            scenario.Scope.TenantId,
            scenario.Authority,
            cancellationToken).ConfigureAwait(false);

        scenario.LedgerId = await scenario.Administration.OpenLedgerAsync(
            new OpenLedgerRequest(scenario.Scope, $"book-{ordinal:D4}"),
            scenario.Authority,
            cancellationToken).ConfigureAwait(false);

        await scenario.Administration.OpenPeriodAsync(
            new OpenPeriodRequest(
                scenario.Scope,
                scenario.LedgerId,
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 12, 31)),
            scenario.Authority,
            cancellationToken).ConfigureAwait(false);

        scenario.FundingAccountId = await scenario.OpenAccountAsync(
            "funding", AccountClass.Equity, PostingDirection.Credit, BalancePolicy.Unrestricted, cancellationToken)
            .ConfigureAwait(false);
        scenario.CustomerAccountAId = await scenario.OpenAccountAsync(
            "customer-a", AccountClass.Liability, PostingDirection.Credit, BalancePolicy.NeverNegative, cancellationToken)
            .ConfigureAwait(false);
        scenario.CustomerAccountBId = await scenario.OpenAccountAsync(
            "customer-b", AccountClass.Liability, PostingDirection.Credit, BalancePolicy.NeverNegative, cancellationToken)
            .ConfigureAwait(false);

        return scenario;
    }

    /// <summary>Opens an additional account in the seeded ledger.</summary>
    public Task<Guid> OpenAccountAsync(
        string code,
        AccountClass accountClass,
        PostingDirection normalSide,
        BalancePolicy policy,
        CancellationToken cancellationToken = default) =>
        Administration.OpenAccountAsync(
            new OpenAccountRequest(Scope, LedgerId, code, AssetId, accountClass, normalSide, $"test:{code}", policy),
            Authority,
            cancellationToken);

    /// <summary>The dates every seeded command uses unless a test overrides them.</summary>
    public static JournalDates DefaultDates { get; } = new(
        new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero),
        new DateOnly(2026, 6, 15),
        new DateOnly(2026, 6, 15),
        new DateOnly(2026, 6, 15));

    /// <summary>Builds a transfer command against the seeded scope.</summary>
    public InternalTransferCommand Transfer(
        Guid debitAccountId,
        Guid creditAccountId,
        long amount,
        string idempotencyKey,
        string principalId = "workload:integration-tests",
        JournalDates? dates = null) =>
        new(
            new IdempotencyScope(
                Scope.TenantId, principalId, InternalTransferCommand.OperationName, idempotencyKey),
            Scope,
            LedgerId,
            debitAccountId,
            creditAccountId,
            Amount.FromCoefficient(amount),
            "integration test transfer",
            ExternalReference: null,
            dates ?? DefaultDates,
            Authority,
            Guid.NewGuid());

    /// <summary>Builds a reversal command against the seeded scope.</summary>
    public ReverseJournalCommand Reversal(
        Guid journalId,
        string idempotencyKey,
        string principalId = "workload:integration-tests") =>
        new(
            new IdempotencyScope(Scope.TenantId, principalId, ReverseJournalCommand.OperationName, idempotencyKey),
            Scope,
            journalId,
            "integration test reversal",
            DefaultDates,
            Authority,
            Guid.NewGuid());

    /// <summary>Funds a customer account from the unrestricted funding account.</summary>
    public async Task<Guid> FundAsync(Guid accountId, long amount, CancellationToken cancellationToken = default)
    {
        var result = await Posting.PostInternalTransferAsync(
            Transfer(FundingAccountId, accountId, amount, $"fund-{Guid.NewGuid():N}"),
            cancellationToken).ConfigureAwait(false);

        Assert.Equal(PostingOutcomeKind.Posted, result.Kind);
        return result.JournalId!.Value;
    }

    /// <summary>Reads an account's posted balance in atomic units.</summary>
    public async Task<Int128> PostedBalanceAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var view = await Query.GetAccountBalanceAsync(Scope.TenantId, accountId, cancellationToken)
            .ConfigureAwait(false);
        Assert.NotNull(view);
        return view.PostedBalance;
    }
}
