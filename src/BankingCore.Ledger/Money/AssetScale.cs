namespace BankingCore.Ledger.Money;

/// <summary>
/// The immutable number of decimal places an asset's atomic unit represents.
/// </summary>
/// <remarks>
/// An asset's scale cannot change after use; a redenomination is a modeled migration or exchange,
/// not a metadata edit (docs/architecture/ledger.md, "Value model"). The upper bound of 18 keeps
/// the presentation of a <c>numeric(38,0)</c> coefficient well inside a representable decimal
/// magnitude while covering minor units, crypto assets, and high-precision unit-of-account assets.
/// </remarks>
public readonly struct AssetScale : IEquatable<AssetScale>
{
    /// <summary>The largest scale this project supports.</summary>
    public const int MaxValue = 18;

    private readonly short _value;

    private AssetScale(short value) => _value = value;

    /// <summary>The number of decimal places.</summary>
    public int Value => _value;

    /// <summary>Creates a scale, rejecting values outside 0..18.</summary>
    public static AssetScale FromInt32(int value)
    {
        if (value is < 0 or > MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Asset scale must be between 0 and {MaxValue}.");
        }

        return new AssetScale((short)value);
    }

    /// <summary>Creates a scale without throwing.</summary>
    public static bool TryFromInt32(int value, out AssetScale scale)
    {
        if (value is < 0 or > MaxValue)
        {
            scale = default;
            return false;
        }

        scale = new AssetScale((short)value);
        return true;
    }

    /// <summary>
    /// Renders a coefficient for human display only. Contracts and storage always carry the exact
    /// integer coefficient; this method must never be used to make an accounting decision.
    /// </summary>
    public string Format(Amount amount)
    {
        var digits = amount.ToString();
        if (_value == 0)
        {
            return digits;
        }

        if (digits.Length <= _value)
        {
            digits = digits.PadLeft(_value + 1, '0');
        }

        var split = digits.Length - _value;
        return string.Concat(digits.AsSpan(0, split), ".", digits.AsSpan(split));
    }

    /// <inheritdoc />
    public bool Equals(AssetScale other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AssetScale other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => _value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator ==(AssetScale left, AssetScale right) => left.Equals(right);

    public static bool operator !=(AssetScale left, AssetScale right) => !left.Equals(right);
}
