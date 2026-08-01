namespace BankingCore.Ledger.Model;

/// <summary>The side of a posting. A posting is never signed; direction carries the sign.</summary>
public enum PostingDirection
{
    /// <summary>Left side of the journal.</summary>
    Debit = 1,

    /// <summary>Right side of the journal.</summary>
    Credit = 2,
}

/// <summary>
/// The accounting classification of a ledger account. Class and normal side together give the
/// declared semantics used to calculate a product-facing signed balance; the project does not
/// assume a universal sign convention (docs/architecture/ledger.md, "Accounting model").
/// </summary>
public enum AccountClass
{
    Asset = 1,
    Liability = 2,
    Equity = 3,
    Income = 4,
    Expense = 5,
}

/// <summary>Lifecycle of a ledger account.</summary>
public enum AccountStatus
{
    /// <summary>Accepts postings subject to policy.</summary>
    Open = 1,

    /// <summary>Readable, but rejects new postings.</summary>
    Frozen = 2,

    /// <summary>Permanently closed to new postings.</summary>
    Closed = 3,
}

/// <summary>Lifecycle of an asset definition.</summary>
public enum AssetStatus
{
    Active = 1,
    Suspended = 2,
    Retired = 3,
}

/// <summary>Lifecycle of an accounting period.</summary>
public enum AccountingPeriodStatus
{
    /// <summary>New effective dates are accepted.</summary>
    Open = 1,

    /// <summary>Only separately authorized adjustments are accepted.</summary>
    Closed = 2,
}

/// <summary>Terminal state of an idempotency receipt.</summary>
public enum IdempotencyOutcome
{
    /// <summary>The command committed a journal.</summary>
    Succeeded = 1,

    /// <summary>The command was deterministically rejected and produced no journal.</summary>
    Failed = 2,
}

/// <summary>Extension helpers mapping domain enums to their stable persisted tokens.</summary>
public static class LedgerEnumTokens
{
    /// <summary>Stable database and contract token for a direction.</summary>
    public static string ToToken(this PostingDirection direction) => direction switch
    {
        PostingDirection.Debit => "debit",
        PostingDirection.Credit => "credit",
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    /// <summary>Parses a persisted direction token.</summary>
    public static PostingDirection ParseDirection(string token) => token switch
    {
        "debit" => PostingDirection.Debit,
        "credit" => PostingDirection.Credit,
        _ => throw new ArgumentOutOfRangeException(nameof(token), "Unknown posting direction token."),
    };

    /// <summary>The opposite side, used to build a reversal.</summary>
    public static PostingDirection Opposite(this PostingDirection direction) =>
        direction == PostingDirection.Debit ? PostingDirection.Credit : PostingDirection.Debit;

    /// <summary>Stable database and contract token for an account class.</summary>
    public static string ToToken(this AccountClass accountClass) => accountClass switch
    {
        AccountClass.Asset => "asset",
        AccountClass.Liability => "liability",
        AccountClass.Equity => "equity",
        AccountClass.Income => "income",
        AccountClass.Expense => "expense",
        _ => throw new ArgumentOutOfRangeException(nameof(accountClass)),
    };

    /// <summary>Parses a persisted account class token.</summary>
    public static AccountClass ParseAccountClass(string token) => token switch
    {
        "asset" => AccountClass.Asset,
        "liability" => AccountClass.Liability,
        "equity" => AccountClass.Equity,
        "income" => AccountClass.Income,
        "expense" => AccountClass.Expense,
        _ => throw new ArgumentOutOfRangeException(nameof(token), "Unknown account class token."),
    };

    /// <summary>Stable database and contract token for an account status.</summary>
    public static string ToToken(this AccountStatus status) => status switch
    {
        AccountStatus.Open => "open",
        AccountStatus.Frozen => "frozen",
        AccountStatus.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>Parses a persisted account status token.</summary>
    public static AccountStatus ParseAccountStatus(string token) => token switch
    {
        "open" => AccountStatus.Open,
        "frozen" => AccountStatus.Frozen,
        "closed" => AccountStatus.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(token), "Unknown account status token."),
    };

    /// <summary>Stable database and contract token for an asset status.</summary>
    public static string ToToken(this AssetStatus status) => status switch
    {
        AssetStatus.Active => "active",
        AssetStatus.Suspended => "suspended",
        AssetStatus.Retired => "retired",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>Parses a persisted asset status token.</summary>
    public static AssetStatus ParseAssetStatus(string token) => token switch
    {
        "active" => AssetStatus.Active,
        "suspended" => AssetStatus.Suspended,
        "retired" => AssetStatus.Retired,
        _ => throw new ArgumentOutOfRangeException(nameof(token), "Unknown asset status token."),
    };
}
