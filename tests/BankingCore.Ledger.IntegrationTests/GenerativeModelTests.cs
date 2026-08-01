using System.Globalization;
using System.Numerics;
using System.Text;
using BankingCore.Ledger.IntegrationTests.Infrastructure;
using BankingCore.Ledger.Model;
using BankingCore.Ledger.Persistence;
using Xunit.Abstractions;

namespace BankingCore.Ledger.IntegrationTests;

/// <summary>
/// Compares long generated command sequences against a small independent reference model.
/// </summary>
/// <remarks>
/// <para>
/// The reference model is deliberately naive: dictionaries and arbitrary-precision integers, no
/// database, no shared code with the implementation beyond the enum tokens. Production code is never
/// used as the oracle for itself (docs/delivery/testing-strategy.md, "Generative/model testing").
/// </para>
/// <para>
/// Every step asserts the outcome the model predicts, and every run finishes by recomputing all
/// balances and running the full reconciliation suite. Seeds are fixed so a failure is reproducible;
/// the failing seed and step index are reported in the assertion message.
/// </para>
/// </remarks>
public sealed class GenerativeModelTests : IAsyncLifetime
{
    private const int StepsPerSeed = 150;

    private readonly LedgerTestDatabase _database = new();
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the fixture.</summary>
    public GenerativeModelTests(ITestOutputHelper output) => _output = output;

    /// <inheritdoc />
    public Task InitializeAsync() => _database.InitializeAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(20260801)]
    [InlineData(4711)]
    [InlineData(99991)]
    public async Task A_generated_command_sequence_agrees_with_the_reference_model(int seed)
    {
        var scenario = await LedgerScenario.CreateAsync(_database);
        var random = new Random(seed);
        var log = new StringBuilder();

        var accounts = new List<ModelAccount>
        {
            new(scenario.FundingAccountId, PostingDirection.Credit, AllowsNegative: true),
            new(scenario.CustomerAccountAId, PostingDirection.Credit, AllowsNegative: false),
            new(scenario.CustomerAccountBId, PostingDirection.Credit, AllowsNegative: false),
        };

        var control = await scenario.OpenAccountAsync(
            "control", AccountClass.Asset, PostingDirection.Debit, BalancePolicy.Unrestricted);
        accounts.Add(new ModelAccount(control, PostingDirection.Debit, AllowsNegative: true));

        var restricted = await scenario.OpenAccountAsync(
            "restricted", AccountClass.Asset, PostingDirection.Debit, BalancePolicy.NeverNegative);
        accounts.Add(new ModelAccount(restricted, PostingDirection.Debit, AllowsNegative: false));

        var model = new ReferenceLedger(accounts);
        var postedJournals = new List<Guid>();

        for (var step = 0; step < StepsPerSeed; step++)
        {
            var action = random.Next(100);
            if (action < 65 || postedJournals.Count == 0)
            {
                await RunTransferStepAsync(scenario, model, accounts, random, step, log, postedJournals);
            }
            else if (action < 80)
            {
                await RunDuplicateStepAsync(scenario, model, accounts, random, step, log);
            }
            else if (action < 90)
            {
                await RunConflictStepAsync(scenario, model, accounts, random, step, log);
            }
            else
            {
                await RunReversalStepAsync(scenario, model, random, step, log, postedJournals);
            }
        }

        _output.WriteLine($"seed {seed}: {StepsPerSeed} steps, {postedJournals.Count} journals posted");

        foreach (var account in accounts)
        {
            var expected = model.PostedBalance(account.AccountId);
            var actual = await scenario.PostedBalanceAsync(account.AccountId);
            Assert.True(
                expected == (BigInteger)actual,
                $"seed {seed}: account {account.AccountId} expected {expected} but the ledger holds {actual}.\n{log}");
        }

        await scenario.Projection.ProjectAsync(scenario.Scope.TenantId, scenario.LedgerId);
        var reconciliation = await scenario.Reconciliation.RunAsync(
            scenario.Scope.TenantId,
            scenario.LedgerId,
            $"generative-model-seed-{seed.ToString(CultureInfo.InvariantCulture)}");

        Assert.True(
            reconciliation.IsClean,
            $"seed {seed}: reconciliation found "
            + string.Join("; ", reconciliation.Breaks.Select(item => $"{item.CheckName}:{item.Subject}")));
    }

    private static async Task RunTransferStepAsync(
        LedgerScenario scenario,
        ReferenceLedger model,
        List<ModelAccount> accounts,
        Random random,
        int step,
        StringBuilder log,
        List<Guid> postedJournals)
    {
        var (debit, credit) = PickPair(accounts, random);
        var amount = PickAmount(random);
        var key = $"gen-{step}";

        var expected = model.PredictTransfer(key, debit, credit, amount);
        var result = await scenario.Posting.PostInternalTransferAsync(
            scenario.Transfer(debit, credit, amount, key));

        log.AppendLine(CultureInfo.InvariantCulture, $"{step}: transfer {amount} {debit:N}->{credit:N} => {result.Kind}");
        AssertOutcome(expected, result, step, log);

        if (result.Kind == PostingOutcomeKind.Posted)
        {
            model.ApplyTransfer(key, result.JournalId!.Value, debit, credit, amount);
            postedJournals.Add(result.JournalId.Value);
        }
        else if (result.Error is not null)
        {
            model.RecordFailure(key, result.Error.Code);
        }
    }

    private static async Task RunDuplicateStepAsync(
        LedgerScenario scenario,
        ReferenceLedger model,
        List<ModelAccount> accounts,
        Random random,
        int step,
        StringBuilder log)
    {
        var replayed = model.AnyKey(random);
        if (replayed is null)
        {
            return;
        }

        var (key, debit, credit, amount) = replayed.Value;
        var expected = model.PredictTransfer(key, debit, credit, amount);
        var result = await scenario.Posting.PostInternalTransferAsync(
            scenario.Transfer(debit, credit, amount, key));

        log.AppendLine(CultureInfo.InvariantCulture, $"{step}: duplicate {key} => {result.Kind}");
        AssertOutcome(expected, result, step, log);
    }

    private static async Task RunConflictStepAsync(
        LedgerScenario scenario,
        ReferenceLedger model,
        List<ModelAccount> accounts,
        Random random,
        int step,
        StringBuilder log)
    {
        var replayed = model.AnyKey(random);
        if (replayed is null)
        {
            return;
        }

        var (key, debit, credit, amount) = replayed.Value;
        var mutated = amount + 1;
        var expected = model.PredictTransfer(key, debit, credit, mutated);
        var result = await scenario.Posting.PostInternalTransferAsync(
            scenario.Transfer(debit, credit, mutated, key));

        log.AppendLine(CultureInfo.InvariantCulture, $"{step}: conflict on {key} => {result.Kind}");
        AssertOutcome(expected, result, step, log);
    }

    private static async Task RunReversalStepAsync(
        LedgerScenario scenario,
        ReferenceLedger model,
        Random random,
        int step,
        StringBuilder log,
        List<Guid> postedJournals)
    {
        var journalId = postedJournals[random.Next(postedJournals.Count)];
        var key = $"gen-reverse-{step}";

        var expected = model.PredictReversal(key, journalId);
        var result = await scenario.Posting.ReverseJournalAsync(scenario.Reversal(journalId, key));

        log.AppendLine(CultureInfo.InvariantCulture, $"{step}: reverse {journalId:N} => {result.Kind}");
        AssertOutcome(expected, result, step, log);

        if (result.Kind == PostingOutcomeKind.Posted)
        {
            model.ApplyReversal(key, result.JournalId!.Value, journalId);
            postedJournals.Add(result.JournalId.Value);
        }
        else if (result.Error is not null)
        {
            model.RecordFailure(key, result.Error.Code);
        }
    }

    private static void AssertOutcome(
        PredictedOutcome expected,
        PostingResult actual,
        int step,
        StringBuilder log)
    {
        // A retryable exhaustion is a transport-level answer the model does not attempt to predict;
        // no other outcome may differ.
        if (actual.Error?.Code == LedgerErrorCode.ConcurrencyRetryExhausted)
        {
            return;
        }

        Assert.True(
            expected.Kind == actual.Kind,
            $"step {step}: expected {expected.Kind} but got {actual.Kind} ({actual.Error?.Token}).\n{log}");

        if (expected.ErrorCode is not null)
        {
            Assert.True(
                expected.ErrorCode == actual.Error?.Code,
                $"step {step}: expected {expected.ErrorCode} but got {actual.Error?.Code}.\n{log}");
        }

        if (expected.JournalId is not null)
        {
            Assert.True(
                expected.JournalId == actual.JournalId,
                $"step {step}: expected journal {expected.JournalId} but got {actual.JournalId}.\n{log}");
        }
    }

    private static (Guid Debit, Guid Credit) PickPair(List<ModelAccount> accounts, Random random)
    {
        var debitIndex = random.Next(accounts.Count);
        int creditIndex;
        do
        {
            creditIndex = random.Next(accounts.Count);
        }
        while (creditIndex == debitIndex);

        return (accounts[debitIndex].AccountId, accounts[creditIndex].AccountId);
    }

    private static long PickAmount(Random random) => random.Next(10) switch
    {
        0 => 1,
        1 => 999_999,
        _ => random.Next(1, 50_000),
    };

    private sealed record ModelAccount(Guid AccountId, PostingDirection NormalSide, bool AllowsNegative);

    private sealed record PredictedOutcome(PostingOutcomeKind Kind, LedgerErrorCode? ErrorCode, Guid? JournalId);

    /// <summary>
    /// The oracle: a dictionary-and-BigInteger model of the ledger rules the generator exercises.
    /// </summary>
    private sealed class ReferenceLedger
    {
        private readonly Dictionary<Guid, ModelAccount> _accounts;
        private readonly Dictionary<Guid, BigInteger> _debits = [];
        private readonly Dictionary<Guid, BigInteger> _credits = [];
        private readonly Dictionary<string, Receipt> _receipts = [];
        private readonly Dictionary<Guid, ModelJournal> _journals = [];
        private readonly HashSet<Guid> _reversed = [];
        private readonly List<string> _transferKeys = [];

        public ReferenceLedger(IEnumerable<ModelAccount> accounts) =>
            _accounts = accounts.ToDictionary(account => account.AccountId);

        public BigInteger PostedBalance(Guid accountId)
        {
            var account = _accounts[accountId];
            _debits.TryGetValue(accountId, out var debit);
            _credits.TryGetValue(accountId, out var credit);
            return account.NormalSide == PostingDirection.Debit ? debit - credit : credit - debit;
        }

        public (string Key, Guid Debit, Guid Credit, long Amount)? AnyKey(Random random)
        {
            if (_transferKeys.Count == 0)
            {
                return null;
            }

            var key = _transferKeys[random.Next(_transferKeys.Count)];
            var receipt = _receipts[key];
            return (key, receipt.DebitAccountId!.Value, receipt.CreditAccountId!.Value, receipt.Amount!.Value);
        }

        public PredictedOutcome PredictTransfer(string key, Guid debit, Guid credit, long amount)
        {
            if (_receipts.TryGetValue(key, out var existing))
            {
                var sameRequest = existing.DebitAccountId == debit
                    && existing.CreditAccountId == credit
                    && existing.Amount == amount;

                if (!sameRequest)
                {
                    return new PredictedOutcome(
                        PostingOutcomeKind.Rejected, LedgerErrorCode.IdempotencyConflict, null);
                }

                return existing.JournalId is not null
                    ? new PredictedOutcome(PostingOutcomeKind.IdempotentReplay, null, existing.JournalId)
                    : new PredictedOutcome(PostingOutcomeKind.Rejected, existing.ErrorCode, null);
            }

            return WouldViolatePolicy([(debit, PostingDirection.Debit, amount), (credit, PostingDirection.Credit, amount)])
                ? new PredictedOutcome(PostingOutcomeKind.Rejected, LedgerErrorCode.BalancePolicyViolation, null)
                : new PredictedOutcome(PostingOutcomeKind.Posted, null, null);
        }

        public PredictedOutcome PredictReversal(string key, Guid journalId)
        {
            if (_receipts.TryGetValue(key, out var existing))
            {
                return existing.JournalId is not null
                    ? new PredictedOutcome(PostingOutcomeKind.IdempotentReplay, null, existing.JournalId)
                    : new PredictedOutcome(PostingOutcomeKind.Rejected, existing.ErrorCode, null);
            }

            var journal = _journals[journalId];
            if (journal.ReversesJournalId is not null)
            {
                return new PredictedOutcome(
                    PostingOutcomeKind.Rejected, LedgerErrorCode.CannotReverseAReversal, null);
            }

            if (_reversed.Contains(journalId))
            {
                return new PredictedOutcome(
                    PostingOutcomeKind.Rejected, LedgerErrorCode.JournalAlreadyReversed, null);
            }

            var mirrored = journal.Legs
                .Select(leg => (leg.AccountId, Direction: Opposite(leg.Direction), leg.Amount))
                .ToArray();

            return WouldViolatePolicy(mirrored)
                ? new PredictedOutcome(PostingOutcomeKind.Rejected, LedgerErrorCode.BalancePolicyViolation, null)
                : new PredictedOutcome(PostingOutcomeKind.Posted, null, null);
        }

        public void ApplyTransfer(string key, Guid journalId, Guid debit, Guid credit, long amount)
        {
            Add(_debits, debit, amount);
            Add(_credits, credit, amount);
            _journals[journalId] = new ModelJournal(
                [(debit, PostingDirection.Debit, amount), (credit, PostingDirection.Credit, amount)],
                ReversesJournalId: null);
            _receipts[key] = new Receipt(journalId, null, debit, credit, amount);
            _transferKeys.Add(key);
        }

        public void ApplyReversal(string key, Guid journalId, Guid originalJournalId)
        {
            var original = _journals[originalJournalId];
            var legs = original.Legs
                .Select(leg => (leg.AccountId, Direction: Opposite(leg.Direction), leg.Amount))
                .ToArray();

            foreach (var leg in legs)
            {
                Add(leg.Direction == PostingDirection.Debit ? _debits : _credits, leg.AccountId, leg.Amount);
            }

            _journals[journalId] = new ModelJournal(legs, originalJournalId);
            _reversed.Add(originalJournalId);
            _receipts[key] = new Receipt(journalId, null, null, null, null);
        }

        public void RecordFailure(string key, LedgerErrorCode code)
        {
            if (code is LedgerErrorCode.IdempotencyConflict or LedgerErrorCode.ConcurrencyRetryExhausted)
            {
                // A conflict does not create a receipt; it reports the one that already exists.
                return;
            }

            if (!_receipts.ContainsKey(key))
            {
                _receipts[key] = new Receipt(null, code, null, null, null);
            }
        }

        private bool WouldViolatePolicy(IReadOnlyList<(Guid AccountId, PostingDirection Direction, long Amount)> legs)
        {
            var debitDeltas = new Dictionary<Guid, BigInteger>();
            var creditDeltas = new Dictionary<Guid, BigInteger>();

            foreach (var leg in legs)
            {
                var side = leg.Direction == PostingDirection.Debit ? debitDeltas : creditDeltas;
                side.TryGetValue(leg.AccountId, out var running);
                side[leg.AccountId] = running + leg.Amount;
            }

            foreach (var accountId in debitDeltas.Keys.Union(creditDeltas.Keys))
            {
                var account = _accounts[accountId];
                if (account.AllowsNegative)
                {
                    continue;
                }

                _debits.TryGetValue(accountId, out var debit);
                _credits.TryGetValue(accountId, out var credit);
                debitDeltas.TryGetValue(accountId, out var debitDelta);
                creditDeltas.TryGetValue(accountId, out var creditDelta);

                var resultingDebit = debit + debitDelta;
                var resultingCredit = credit + creditDelta;
                var resulting = account.NormalSide == PostingDirection.Debit
                    ? resultingDebit - resultingCredit
                    : resultingCredit - resultingDebit;

                if (resulting < BigInteger.Zero)
                {
                    return true;
                }
            }

            return false;
        }

        private static PostingDirection Opposite(PostingDirection direction) =>
            direction == PostingDirection.Debit ? PostingDirection.Credit : PostingDirection.Debit;

        private static void Add(Dictionary<Guid, BigInteger> totals, Guid accountId, long amount)
        {
            totals.TryGetValue(accountId, out var running);
            totals[accountId] = running + amount;
        }

        private sealed record Receipt(
            Guid? JournalId,
            LedgerErrorCode? ErrorCode,
            Guid? DebitAccountId,
            Guid? CreditAccountId,
            long? Amount);

        private sealed record ModelJournal(
            IReadOnlyList<(Guid AccountId, PostingDirection Direction, long Amount)> Legs,
            Guid? ReversesJournalId);
    }
}
