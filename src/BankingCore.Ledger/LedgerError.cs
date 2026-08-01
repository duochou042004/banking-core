namespace BankingCore.Ledger;

/// <summary>
/// Stable machine-readable rejection codes.
/// </summary>
/// <remarks>
/// These codes are part of the public contract: they appear as the RFC 9457 problem type suffix and
/// as the stored terminal outcome of a failed idempotency receipt. Renaming one is a breaking
/// change (docs/architecture/integration.md, "HTTP APIs").
/// </remarks>
public enum LedgerErrorCode
{
    /// <summary>The journal has fewer than two postings.</summary>
    JournalTooFewPostings = 1,

    /// <summary>A posting amount was zero.</summary>
    ZeroPostingAmount = 2,

    /// <summary>Debits did not equal credits for a (ledger, asset) group.</summary>
    JournalNotBalanced = 3,

    /// <summary>Two postings shared a posting order.</summary>
    DuplicatePostingOrder = 4,

    /// <summary>A referenced account does not exist within the caller's scope.</summary>
    UnknownAccount = 5,

    /// <summary>A referenced account belongs to a different ledger than the journal.</summary>
    AccountLedgerMismatch = 6,

    /// <summary>A referenced account is not open for posting.</summary>
    AccountNotOpen = 7,

    /// <summary>The account's asset is not active.</summary>
    AssetNotActive = 8,

    /// <summary>The resulting balance violates the account's declared balance policy.</summary>
    BalancePolicyViolation = 9,

    /// <summary>The effective date falls in a closed or undefined accounting period.</summary>
    AccountingPeriodClosed = 10,

    /// <summary>The idempotency key was reused with a different request fingerprint.</summary>
    IdempotencyConflict = 11,

    /// <summary>The journal to reverse does not exist within the caller's scope.</summary>
    UnknownJournal = 12,

    /// <summary>The journal to reverse has already been reversed.</summary>
    JournalAlreadyReversed = 13,

    /// <summary>A reversal was requested for a journal that is itself a reversal.</summary>
    CannotReverseAReversal = 14,

    /// <summary>An amount or aggregate would leave the supported numeric range.</summary>
    AmountOutOfRange = 15,

    /// <summary>The request referenced a ledger that does not exist within the caller's scope.</summary>
    UnknownLedger = 16,

    /// <summary>The posting path exhausted its serialization-failure retry budget.</summary>
    ConcurrencyRetryExhausted = 17,

    /// <summary>The request body failed structural validation.</summary>
    MalformedRequest = 18,
}

/// <summary>A deterministic rejection with a stable code and a safe, non-sensitive detail string.</summary>
/// <param name="Code">The stable rejection code.</param>
/// <param name="Detail">
/// Operator-readable explanation. Must never contain restricted personal data, secrets, or raw
/// request bodies (docs/architecture/data-and-consistency.md, "Sensitive data").
/// </param>
public sealed record LedgerError(LedgerErrorCode Code, string Detail)
{
    /// <summary>The kebab-case token used in contracts and stored outcomes.</summary>
    public string Token => Code switch
    {
        LedgerErrorCode.JournalTooFewPostings => "journal-too-few-postings",
        LedgerErrorCode.ZeroPostingAmount => "zero-posting-amount",
        LedgerErrorCode.JournalNotBalanced => "journal-not-balanced",
        LedgerErrorCode.DuplicatePostingOrder => "duplicate-posting-order",
        LedgerErrorCode.UnknownAccount => "unknown-account",
        LedgerErrorCode.AccountLedgerMismatch => "account-ledger-mismatch",
        LedgerErrorCode.AccountNotOpen => "account-not-open",
        LedgerErrorCode.AssetNotActive => "asset-not-active",
        LedgerErrorCode.BalancePolicyViolation => "balance-policy-violation",
        LedgerErrorCode.AccountingPeriodClosed => "accounting-period-closed",
        LedgerErrorCode.IdempotencyConflict => "idempotency-conflict",
        LedgerErrorCode.UnknownJournal => "unknown-journal",
        LedgerErrorCode.JournalAlreadyReversed => "journal-already-reversed",
        LedgerErrorCode.CannotReverseAReversal => "cannot-reverse-a-reversal",
        LedgerErrorCode.AmountOutOfRange => "amount-out-of-range",
        LedgerErrorCode.UnknownLedger => "unknown-ledger",
        LedgerErrorCode.ConcurrencyRetryExhausted => "concurrency-retry-exhausted",
        LedgerErrorCode.MalformedRequest => "malformed-request",
        _ => throw new ArgumentOutOfRangeException(nameof(Code)),
    };

    /// <summary>
    /// True when a client may reasonably retry the same command unchanged. Only transient
    /// concurrency exhaustion qualifies; every other code is a deterministic rejection whose
    /// terminal outcome is stored against the idempotency key.
    /// </summary>
    public bool IsRetryable => Code == LedgerErrorCode.ConcurrencyRetryExhausted;
}

/// <summary>Thrown when a ledger command is deterministically rejected.</summary>
public sealed class LedgerRejectedException : Exception
{
    /// <summary>Creates the exception from a rejection.</summary>
    public LedgerRejectedException(LedgerError error)
        : base(error.Detail) => Error = error;

    /// <summary>The rejection.</summary>
    public LedgerError Error { get; }
}
