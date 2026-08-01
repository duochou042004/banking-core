namespace BankingCore.Ledger.Money;

/// <summary>
/// A non-negative monetary or asset quantity expressed as an integer coefficient of atomic units.
/// </summary>
/// <remarks>
/// The ledger constitution (docs/architecture/ledger.md, "Value model") requires amounts to be a
/// non-negative integer coefficient in atomic units plus the asset's immutable scale, stored as
/// <c>numeric(38,0)</c>, with binary floating point forbidden. <see cref="Int128"/> is the smallest
/// exact CLR integer that covers the whole <c>numeric(38,0)</c> domain: the largest 38-digit value
/// is 10^38-1, which is below <see cref="Int128.MaxValue"/>. Scale lives on the asset, never on the
/// amount, so an amount is meaningless without its <see cref="AssetScale"/>.
/// </remarks>
public readonly struct Amount : IEquatable<Amount>, IComparable<Amount>
{
    /// <summary>The largest coefficient representable in <c>numeric(38,0)</c>: 10^38 - 1.</summary>
    public static readonly Int128 MaxCoefficient = Int128.Parse(
        "99999999999999999999999999999999999999",
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The additive identity. A zero amount is valid as an aggregate but never as a posting.</summary>
    public static Amount Zero => default;

    private readonly Int128 _coefficient;

    private Amount(Int128 coefficient) => _coefficient = coefficient;

    /// <summary>The integer count of atomic units.</summary>
    public Int128 Coefficient => _coefficient;

    /// <summary>True when the coefficient is zero.</summary>
    public bool IsZero => _coefficient == Int128.Zero;

    /// <summary>True when the coefficient is strictly greater than zero.</summary>
    public bool IsPositive => _coefficient > Int128.Zero;

    /// <summary>
    /// Creates an amount from an integer coefficient, rejecting negatives and values outside
    /// <c>numeric(38,0)</c>.
    /// </summary>
    public static Amount FromCoefficient(Int128 coefficient)
    {
        if (!TryFromCoefficient(coefficient, out var amount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(coefficient),
                "Amount coefficients must be between 0 and 10^38-1 inclusive.");
        }

        return amount;
    }

    /// <summary>Creates an amount from an integer coefficient without throwing.</summary>
    public static bool TryFromCoefficient(Int128 coefficient, out Amount amount)
    {
        if (coefficient < Int128.Zero || coefficient > MaxCoefficient)
        {
            amount = default;
            return false;
        }

        amount = new Amount(coefficient);
        return true;
    }

    /// <summary>
    /// Parses the canonical wire form: an optionally signed sequence of decimal digits with no
    /// separators, exponent, or fractional part. JSON contracts encode coefficients as strings so
    /// that no consumer silently narrows the range (docs/architecture/ledger.md, "Value model").
    /// </summary>
    public static bool TryParse(string? text, out Amount amount)
    {
        amount = default;
        if (string.IsNullOrEmpty(text) || text.Length > 39)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return Int128.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var coefficient)
            && TryFromCoefficient(coefficient, out amount);
    }

    /// <summary>Parses the canonical wire form or throws.</summary>
    public static Amount Parse(string text) =>
        TryParse(text, out var amount)
            ? amount
            : throw new FormatException("An amount must be an unsigned decimal integer within numeric(38,0).");

    /// <summary>
    /// Adds two amounts, refusing to produce a value outside <c>numeric(38,0)</c>. The guard is
    /// evaluated before the addition so that <see cref="Int128"/> itself cannot wrap.
    /// </summary>
    public static bool TryAdd(Amount left, Amount right, out Amount result)
    {
        if (left._coefficient > MaxCoefficient - right._coefficient)
        {
            result = default;
            return false;
        }

        result = new Amount(left._coefficient + right._coefficient);
        return true;
    }

    /// <summary>Adds two amounts or throws on range overflow.</summary>
    public static Amount Add(Amount left, Amount right) =>
        TryAdd(left, right, out var result)
            ? result
            : throw new OverflowException("The sum of the amounts exceeds numeric(38,0).");

    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/>, refusing to go negative.</summary>
    public static bool TrySubtract(Amount left, Amount right, out Amount result)
    {
        if (left._coefficient < right._coefficient)
        {
            result = default;
            return false;
        }

        result = new Amount(left._coefficient - right._coefficient);
        return true;
    }

    /// <summary>
    /// The signed difference between two amounts. Used for balance projections where a normal-side
    /// convention makes a negative result meaningful; postings themselves are always positive.
    /// </summary>
    public static Int128 SignedDifference(Amount left, Amount right) => left._coefficient - right._coefficient;

    /// <inheritdoc />
    public bool Equals(Amount other) => _coefficient == other._coefficient;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Amount other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _coefficient.GetHashCode();

    /// <inheritdoc />
    public int CompareTo(Amount other) => _coefficient.CompareTo(other._coefficient);

    /// <summary>Renders the canonical wire form: unsigned decimal digits, invariant culture.</summary>
    public override string ToString() => _coefficient.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator ==(Amount left, Amount right) => left.Equals(right);

    public static bool operator !=(Amount left, Amount right) => !left.Equals(right);

    public static bool operator <(Amount left, Amount right) => left.CompareTo(right) < 0;

    public static bool operator <=(Amount left, Amount right) => left.CompareTo(right) <= 0;

    public static bool operator >(Amount left, Amount right) => left.CompareTo(right) > 0;

    public static bool operator >=(Amount left, Amount right) => left.CompareTo(right) >= 0;

    public static Amount operator +(Amount left, Amount right) => Add(left, right);
}
