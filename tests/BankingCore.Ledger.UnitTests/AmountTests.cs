using BankingCore.Ledger.Money;

namespace BankingCore.Ledger.UnitTests;

/// <summary>
/// Exactness and range behaviour of the money type.
/// </summary>
/// <remarks>
/// These are the conformance vectors for the value model in docs/architecture/ledger.md: maximum and
/// minimum amounts, overflow, invalid scale, and string round-trip.
/// </remarks>
public sealed class AmountTests
{
    private const string MaxCoefficientText = "99999999999999999999999999999999999999";

    [Fact]
    public void The_maximum_coefficient_is_the_largest_value_numeric_38_0_can_hold()
    {
        Assert.Equal(MaxCoefficientText, Amount.MaxCoefficient.ToString());
        Assert.Equal(38, MaxCoefficientText.Length);
        Assert.True(Amount.MaxCoefficient < Int128.MaxValue);
    }

    [Fact]
    public void The_maximum_coefficient_round_trips_through_its_string_form()
    {
        var amount = Amount.Parse(MaxCoefficientText);

        Assert.Equal(MaxCoefficientText, amount.ToString());
        Assert.Equal(Amount.MaxCoefficient, amount.Coefficient);
    }

    [Fact]
    public void A_coefficient_beyond_the_supported_range_is_refused()
    {
        Assert.False(Amount.TryFromCoefficient(Amount.MaxCoefficient + Int128.One, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Amount.FromCoefficient(Amount.MaxCoefficient + Int128.One));
    }

    [Fact]
    public void A_negative_coefficient_is_refused()
    {
        Assert.False(Amount.TryFromCoefficient(Int128.NegativeOne, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => Amount.FromCoefficient(Int128.NegativeOne));
    }

    [Fact]
    public void Addition_that_would_leave_the_supported_range_is_refused_before_it_can_wrap()
    {
        var max = Amount.FromCoefficient(Amount.MaxCoefficient);
        var one = Amount.FromCoefficient(Int128.One);

        Assert.False(Amount.TryAdd(max, one, out _));
        Assert.Throws<OverflowException>(() => Amount.Add(max, one));

        // Two near-maximum amounts would overflow Int128 itself if the guard ran after the addition.
        var almost = Amount.FromCoefficient(Amount.MaxCoefficient - Int128.One);
        Assert.False(Amount.TryAdd(almost, almost, out _));
    }

    [Fact]
    public void Addition_inside_the_range_is_exact()
    {
        var left = Amount.Parse("49999999999999999999999999999999999999");
        var right = Amount.Parse("50000000000000000000000000000000000000");

        Assert.True(Amount.TryAdd(left, right, out var sum));
        Assert.Equal(MaxCoefficientText, sum.ToString());
    }

    [Fact]
    public void Subtraction_never_produces_a_negative_amount()
    {
        var five = Amount.FromCoefficient(5);
        var seven = Amount.FromCoefficient(7);

        Assert.False(Amount.TrySubtract(five, seven, out _));
        Assert.True(Amount.TrySubtract(seven, five, out var difference));
        Assert.Equal("2", difference.ToString());
    }

    [Fact]
    public void The_signed_difference_carries_the_sign_a_balance_calculation_needs()
    {
        var five = Amount.FromCoefficient(5);
        var seven = Amount.FromCoefficient(7);

        Assert.Equal((Int128)(-2), Amount.SignedDifference(five, seven));
        Assert.Equal((Int128)2, Amount.SignedDifference(seven, five));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("1_000")]
    [InlineData("1 000")]
    [InlineData("0x10")]
    [InlineData("100000000000000000000000000000000000000")]
    public void A_value_that_is_not_an_unsigned_in_range_integer_is_refused(string text)
    {
        Assert.False(Amount.TryParse(text, out _));
        Assert.Throws<FormatException>(() => Amount.Parse(text));
    }

    [Fact]
    public void Zero_is_a_valid_aggregate_but_reports_itself_as_non_positive()
    {
        Assert.True(Amount.Zero.IsZero);
        Assert.False(Amount.Zero.IsPositive);
        Assert.Equal("0", Amount.Zero.ToString());
    }

    [Theory]
    [InlineData(0, "1500", "1500")]
    [InlineData(2, "1500", "15.00")]
    [InlineData(2, "5", "0.05")]
    [InlineData(2, "0", "0.00")]
    [InlineData(8, "1", "0.00000001")]
    [InlineData(18, "1000000000000000000", "1.000000000000000000")]
    public void Formatting_places_the_decimal_point_from_the_assets_scale(int scale, string coefficient, string expected)
    {
        var formatted = AssetScale.FromInt32(scale).Format(Amount.Parse(coefficient));

        Assert.Equal(expected, formatted);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(19)]
    [InlineData(int.MaxValue)]
    public void An_asset_scale_outside_the_supported_range_is_refused(int scale)
    {
        Assert.False(AssetScale.TryFromInt32(scale, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetScale.FromInt32(scale));
    }

    [Fact]
    public void Amounts_compare_by_coefficient()
    {
        var small = Amount.FromCoefficient(1);
        var large = Amount.FromCoefficient(2);

        Assert.True(small < large);
        Assert.True(large > small);
        Assert.True(small <= Amount.FromCoefficient(1));
        Assert.True(small >= Amount.FromCoefficient(1));
        Assert.Equal(small, Amount.FromCoefficient(1));
        Assert.NotEqual(small, large);
        Assert.Equal(small.GetHashCode(), Amount.FromCoefficient(1).GetHashCode());
    }
}
