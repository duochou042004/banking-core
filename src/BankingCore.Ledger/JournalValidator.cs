using BankingCore.Ledger.Model;
using BankingCore.Ledger.Money;

namespace BankingCore.Ledger;

/// <summary>The account, asset, and authoritative aggregates a single posting decision depends on.</summary>
/// <param name="Account">The account definition.</param>
/// <param name="Asset">The account's asset definition.</param>
/// <param name="Balance">The authoritative aggregates read inside the posting transaction.</param>
public sealed record AccountPostingContext(LedgerAccount Account, Asset Asset, AccountBalance Balance);

/// <summary>The aggregate change one journal makes to one account.</summary>
/// <param name="AccountId">The affected account.</param>
/// <param name="DebitDelta">Total debit added by this journal.</param>
/// <param name="CreditDelta">Total credit added by this journal.</param>
/// <param name="PostingCount">Number of legs this journal contributes to the account.</param>
/// <param name="ResultingDebitTotal">The account's debit total after the journal commits.</param>
/// <param name="ResultingCreditTotal">The account's credit total after the journal commits.</param>
public sealed record AccountDelta(
    Guid AccountId,
    Amount DebitDelta,
    Amount CreditDelta,
    long PostingCount,
    Amount ResultingDebitTotal,
    Amount ResultingCreditTotal);

/// <summary>A journal that satisfied every ledger invariant, with its computed aggregate deltas.</summary>
/// <param name="Draft">The validated instruction.</param>
/// <param name="Deltas">One entry per affected account, ordered by account identifier.</param>
public sealed record ValidatedJournal(JournalDraft Draft, IReadOnlyList<AccountDelta> Deltas);

/// <summary>
/// The pure invariant engine for a proposed journal.
/// </summary>
/// <remarks>
/// <para>
/// This type performs no I/O and holds no state, so the same rules can be exercised by unit tests,
/// by the generative model test, and by the posting path against a real database. It is one of two
/// independent defenses: the database also rejects the same violations through constraints and
/// deferred constraint triggers (docs/architecture/ledger.md, "Required database defenses").
/// </para>
/// <para>
/// Check order is part of the contract. A journal that violates several rules is reported against
/// the first rule in the order below, so a client sees a deterministic code.
/// </para>
/// </remarks>
public static class JournalValidator
{
    /// <summary>The minimum number of legs in a journal.</summary>
    public const int MinimumPostings = 2;

    /// <summary>
    /// Validates a proposed journal against the resolved account contexts and period state.
    /// </summary>
    /// <param name="draft">The proposed journal.</param>
    /// <param name="accounts">
    /// Account contexts read inside the posting transaction, keyed by account identifier. A missing
    /// key is reported as <see cref="LedgerErrorCode.UnknownAccount"/>; the caller must not
    /// substitute a placeholder.
    /// </param>
    /// <param name="isEffectivePeriodOpen">
    /// Whether an accounting period covering <see cref="JournalDates.EffectiveAt"/> exists and is open.
    /// </param>
    /// <param name="validated">The validated journal and its aggregate deltas, when accepted.</param>
    /// <returns>The first violated rule, or <see langword="null"/> when the journal is acceptable.</returns>
    public static LedgerError? Validate(
        JournalDraft draft,
        IReadOnlyDictionary<Guid, AccountPostingContext> accounts,
        bool isEffectivePeriodOpen,
        out ValidatedJournal? validated)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(accounts);
        validated = null;

        if (draft.Postings.Count < MinimumPostings)
        {
            return new LedgerError(
                LedgerErrorCode.JournalTooFewPostings,
                $"A journal requires at least {MinimumPostings} postings; {draft.Postings.Count} were supplied.");
        }

        var seenOrders = new HashSet<int>();
        foreach (var posting in draft.Postings)
        {
            if (posting.PostingOrder < 1)
            {
                return new LedgerError(
                    LedgerErrorCode.DuplicatePostingOrder,
                    "Posting order must start at 1 and increase without repetition.");
            }

            if (!seenOrders.Add(posting.PostingOrder))
            {
                return new LedgerError(
                    LedgerErrorCode.DuplicatePostingOrder,
                    $"Posting order {posting.PostingOrder} appears more than once in the journal.");
            }
        }

        foreach (var posting in draft.Postings)
        {
            if (!posting.Amount.IsPositive)
            {
                return new LedgerError(
                    LedgerErrorCode.ZeroPostingAmount,
                    $"Posting {posting.PostingOrder} has a non-positive amount; every posting must move value.");
            }
        }

        foreach (var posting in draft.Postings)
        {
            if (!accounts.TryGetValue(posting.AccountId, out var context))
            {
                return new LedgerError(
                    LedgerErrorCode.UnknownAccount,
                    $"Posting {posting.PostingOrder} references an account that does not exist in this scope.");
            }

            if (context.Account.LedgerId != draft.LedgerId)
            {
                return new LedgerError(
                    LedgerErrorCode.AccountLedgerMismatch,
                    $"Posting {posting.PostingOrder} references an account in a different ledger; a journal cannot mix ledgers.");
            }

            if (context.Account.Scope != draft.Scope)
            {
                return new LedgerError(
                    LedgerErrorCode.UnknownAccount,
                    $"Posting {posting.PostingOrder} references an account that does not exist in this scope.");
            }

            if (context.Account.Status != AccountStatus.Open)
            {
                return new LedgerError(
                    LedgerErrorCode.AccountNotOpen,
                    $"Posting {posting.PostingOrder} references an account whose status is {context.Account.Status.ToToken()}.");
            }

            if (context.Asset.Status != AssetStatus.Active)
            {
                return new LedgerError(
                    LedgerErrorCode.AssetNotActive,
                    $"Posting {posting.PostingOrder} references asset {context.Asset.Code}, whose status is {context.Asset.Status.ToToken()}.");
            }
        }

        if (!isEffectivePeriodOpen)
        {
            return new LedgerError(
                LedgerErrorCode.AccountingPeriodClosed,
                "No open accounting period covers the requested effective date.");
        }

        var balanceError = CheckPerAssetBalance(draft, accounts);
        if (balanceError is not null)
        {
            return balanceError;
        }

        return BuildDeltas(draft, accounts, out validated);
    }

    /// <summary>
    /// Proves debits equal credits within every (ledger, asset) group. Because a journal cannot mix
    /// ledgers, grouping by asset is sufficient here; the database trigger groups by both.
    /// </summary>
    private static LedgerError? CheckPerAssetBalance(
        JournalDraft draft,
        IReadOnlyDictionary<Guid, AccountPostingContext> accounts)
    {
        var debits = new Dictionary<Guid, Amount>();
        var credits = new Dictionary<Guid, Amount>();

        foreach (var posting in draft.Postings)
        {
            var assetId = accounts[posting.AccountId].Account.AssetId;
            var side = posting.Direction == PostingDirection.Debit ? debits : credits;
            side.TryGetValue(assetId, out var running);
            if (!Amount.TryAdd(running, posting.Amount, out var updated))
            {
                return new LedgerError(
                    LedgerErrorCode.AmountOutOfRange,
                    "The journal total for an asset exceeds the supported numeric range.");
            }

            side[assetId] = updated;
        }

        foreach (var assetId in debits.Keys.Union(credits.Keys))
        {
            debits.TryGetValue(assetId, out var debitTotal);
            credits.TryGetValue(assetId, out var creditTotal);
            if (debitTotal != creditTotal)
            {
                return new LedgerError(
                    LedgerErrorCode.JournalNotBalanced,
                    "Debits do not equal credits for every asset in the journal. Cross-asset exchanges "
                        + "require separately balanced legs through explicit position or clearing accounts.");
            }
        }

        return null;
    }

    /// <summary>
    /// Folds the postings into per-account aggregate deltas and applies the declared balance policy
    /// to the resulting aggregates rather than to the pre-journal aggregates.
    /// </summary>
    private static LedgerError? BuildDeltas(
        JournalDraft draft,
        IReadOnlyDictionary<Guid, AccountPostingContext> accounts,
        out ValidatedJournal? validated)
    {
        validated = null;
        var debitDeltas = new Dictionary<Guid, Amount>();
        var creditDeltas = new Dictionary<Guid, Amount>();
        var counts = new Dictionary<Guid, long>();

        foreach (var posting in draft.Postings)
        {
            var side = posting.Direction == PostingDirection.Debit ? debitDeltas : creditDeltas;
            side.TryGetValue(posting.AccountId, out var running);
            if (!Amount.TryAdd(running, posting.Amount, out var updated))
            {
                return new LedgerError(
                    LedgerErrorCode.AmountOutOfRange,
                    "The journal total for an account exceeds the supported numeric range.");
            }

            side[posting.AccountId] = updated;
            counts.TryGetValue(posting.AccountId, out var count);
            counts[posting.AccountId] = count + 1;
        }

        var deltas = new List<AccountDelta>(counts.Count);
        foreach (var accountId in counts.Keys.Order())
        {
            var context = accounts[accountId];
            debitDeltas.TryGetValue(accountId, out var debitDelta);
            creditDeltas.TryGetValue(accountId, out var creditDelta);

            if (!Amount.TryAdd(context.Balance.DebitTotal, debitDelta, out var resultingDebit)
                || !Amount.TryAdd(context.Balance.CreditTotal, creditDelta, out var resultingCredit))
            {
                return new LedgerError(
                    LedgerErrorCode.AmountOutOfRange,
                    "The resulting account aggregate exceeds the supported numeric range.");
            }

            if (!context.Account.BalancePolicy.AllowsNegativeBalance)
            {
                var resulting = context.Account.NormalSide == PostingDirection.Debit
                    ? Amount.SignedDifference(resultingDebit, resultingCredit)
                    : Amount.SignedDifference(resultingCredit, resultingDebit);

                if (resulting < Int128.Zero)
                {
                    return new LedgerError(
                        LedgerErrorCode.BalancePolicyViolation,
                        $"The journal would drive an account below zero under balance policy "
                            + $"'{context.Account.BalancePolicy.Name}'.");
                }
            }

            deltas.Add(new AccountDelta(
                accountId,
                debitDelta,
                creditDelta,
                counts[accountId],
                resultingDebit,
                resultingCredit));
        }

        validated = new ValidatedJournal(draft, deltas);
        return null;
    }

    /// <summary>
    /// Builds the reversal of a posted journal: the same accounts and amounts on the opposite side,
    /// as a new independently balanced journal linked to the original. The original is never edited
    /// (docs/architecture/ledger.md, "Immutability and correction").
    /// </summary>
    /// <param name="original">The journal being reversed.</param>
    /// <param name="dates">Dates for the reversing journal.</param>
    /// <param name="authority">Authenticated actor and authorization decision for the reversal.</param>
    /// <param name="reason">Operator-supplied reason, free of restricted personal data.</param>
    /// <param name="correlationId">Correlation identifier of the reversal operation.</param>
    /// <returns>A draft that reverses <paramref name="original"/>.</returns>
    public static JournalDraft BuildReversal(
        PostedJournal original,
        JournalDates dates,
        CommandAuthority authority,
        string reason,
        Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(original);

        var postings = original.Postings
            .OrderBy(posting => posting.PostingOrder)
            .Select(posting => new PostingDraft(
                posting.PostingOrder,
                posting.AccountId,
                posting.Direction.Opposite(),
                posting.Amount))
            .ToArray();

        return new JournalDraft(
            original.LedgerId,
            original.Scope,
            original.TransactionType + ".reversal",
            SchemaVersion: 1,
            reason,
            ExternalReference: null,
            dates,
            authority,
            correlationId,
            CausationId: original.JournalId,
            postings,
            ReversesJournalId: original.JournalId);
    }
}
