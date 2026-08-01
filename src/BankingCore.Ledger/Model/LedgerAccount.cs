using BankingCore.Ledger.Money;

namespace BankingCore.Ledger.Model;

/// <summary>An asset definition: the unit in which amounts are counted.</summary>
/// <param name="AssetId">Stable internal identifier.</param>
/// <param name="Code">Human-facing code, unique across the deployment.</param>
/// <param name="Scale">Immutable number of decimal places of the atomic unit.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="ExternalStandard">Optional external standard name, such as <c>iso-4217</c>.</param>
/// <param name="ExternalCode">Optional identifier within that standard.</param>
public sealed record Asset(
    Guid AssetId,
    string Code,
    AssetScale Scale,
    AssetStatus Status,
    string? ExternalStandard,
    string? ExternalCode);

/// <summary>A ledger: the boundary within which journals balance and sequence.</summary>
/// <param name="LedgerId">Stable internal identifier.</param>
/// <param name="Scope">Tenant and legal entity that own the ledger.</param>
/// <param name="Code">Human-facing code, unique within the tenant.</param>
public sealed record LedgerBook(Guid LedgerId, LedgerScope Scope, string Code);

/// <summary>
/// The state of a ledger account that the posting decision depends on.
/// </summary>
/// <remarks>
/// The slice constrains an account to a single asset. Multi-asset accounts are deliberately out of
/// scope; see docs/delivery/task-packets/2026-08-01-ledger-kernel-slice-1.md.
/// </remarks>
/// <param name="AccountId">Stable internal identifier.</param>
/// <param name="LedgerId">Owning ledger.</param>
/// <param name="Scope">Tenant and legal entity that own the account.</param>
/// <param name="Code">Human-facing code, unique within the ledger.</param>
/// <param name="AssetId">The single asset this account may hold.</param>
/// <param name="AccountClass">Accounting classification.</param>
/// <param name="NormalSide">The side on which a positive product-facing balance accumulates.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="BalancePolicy">The named policy governing whether the balance may go negative.</param>
public sealed record LedgerAccount(
    Guid AccountId,
    Guid LedgerId,
    LedgerScope Scope,
    string Code,
    Guid AssetId,
    AccountClass AccountClass,
    PostingDirection NormalSide,
    AccountStatus Status,
    BalancePolicy BalancePolicy);

/// <summary>
/// The versioned policy that decides whether a resulting balance is permitted.
/// </summary>
/// <remarks>
/// Negative availability or balance is allowed only by an explicit versioned policy
/// (docs/architecture/ledger.md, "Holds and availability"). Slice 1 has no holds, credit limits, or
/// overdraft lines, so the only policies are "never negative" and "unrestricted", and available
/// balance equals posted balance.
/// </remarks>
/// <param name="Name">Stable policy name recorded with the account.</param>
/// <param name="AllowsNegativeBalance">Whether the normal-side balance may fall below zero.</param>
public sealed record BalancePolicy(string Name, bool AllowsNegativeBalance)
{
    /// <summary>Customer-facing accounts that may not go negative without a credit product.</summary>
    public static BalancePolicy NeverNegative { get; } = new("posted-only-never-negative-v1", false);

    /// <summary>Internal control, clearing, and equity accounts that may hold either sign.</summary>
    public static BalancePolicy Unrestricted { get; } = new("posted-only-unrestricted-v1", true);

    /// <summary>Resolves a persisted policy name.</summary>
    public static BalancePolicy FromName(string name) => name switch
    {
        "posted-only-never-negative-v1" => NeverNegative,
        "posted-only-unrestricted-v1" => Unrestricted,
        _ => throw new ArgumentOutOfRangeException(nameof(name), $"Unknown balance policy '{name}'."),
    };
}

/// <summary>
/// The authoritative debit and credit aggregates of one account.
/// </summary>
/// <remarks>
/// Debit and credit totals are the primary facts. A signed balance is a calculation over them using
/// the account's declared normal side, never a stored universal-sign number
/// (docs/architecture/ledger.md, "Accounting model").
/// </remarks>
/// <param name="AccountId">The account these aggregates belong to.</param>
/// <param name="AssetId">The asset the aggregates are denominated in.</param>
/// <param name="DebitTotal">Sum of every posted debit.</param>
/// <param name="CreditTotal">Sum of every posted credit.</param>
/// <param name="PostingCount">Number of postings folded into the aggregates.</param>
/// <param name="Version">Optimistic-concurrency version, incremented once per committing journal.</param>
public sealed record AccountBalance(
    Guid AccountId,
    Guid AssetId,
    Amount DebitTotal,
    Amount CreditTotal,
    long PostingCount,
    long Version)
{
    /// <summary>
    /// The product-facing posted balance in atomic units, signed against the account's normal side.
    /// </summary>
    public Int128 PostedBalance(PostingDirection normalSide) =>
        normalSide == PostingDirection.Debit
            ? Amount.SignedDifference(DebitTotal, CreditTotal)
            : Amount.SignedDifference(CreditTotal, DebitTotal);

    /// <summary>
    /// The available balance under the slice-1 policy, which has no holds, limits, or overdraft
    /// lines. Account Servicing replaces this in Phase 2.
    /// </summary>
    public Int128 AvailableBalance(PostingDirection normalSide) => PostedBalance(normalSide);
}
