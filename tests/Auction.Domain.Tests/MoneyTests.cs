using Auction.Contracts;
using Auction.Domain;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 100)]
    [InlineData(12.34, 1234)]
    [InlineData(999999.99, 99999999)]
    public void Cents_round_trip(double dollars, long cents)
    {
        var money = Money.FromCents(cents, "USD");

        money.Amount.Should().Be((decimal)dollars);
        money.ToCents().Should().Be(cents);
    }

    [Fact]
    public void Rounds_to_four_decimals_away_from_zero()
    {
        new Money(0.00005m, "USD").Amount.Should().Be(0.0001m);   // banker's rounding would give 0.0000
        new Money(0.00015m, "USD").Amount.Should().Be(0.0002m);   // banker's rounding would give 0.0002 too
        new Money(-0.00005m, "USD").Amount.Should().Be(-0.0001m);
        new Money(1.23454m, "USD").Amount.Should().Be(1.2345m);
    }

    [Fact]
    public void ToCents_rounds_half_cent_away_from_zero()
    {
        new Money(0.005m, "USD").ToCents().Should().Be(1);   // banker's rounding would give 0
        new Money(0.015m, "USD").ToCents().Should().Be(2);
        new Money(-0.005m, "USD").ToCents().Should().Be(-1);
    }

    [Fact]
    public void Currency_is_upper_cased()
    {
        new Money(1m, "usd").Currency.Should().Be("USD");
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USDX")]
    public void Bad_currency_throws(string currency)
    {
        var act = () => new Money(1m, currency);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Add_and_subtract_same_currency()
    {
        (new Money(10m, "USD") + new Money(2.5m, "USD")).Amount.Should().Be(12.5m);
        (new Money(10m, "USD") - new Money(2.5m, "USD")).Amount.Should().Be(7.5m);
    }

    [Fact]
    public void Mixed_currency_throws()
    {
        var act = () => new Money(1m, "USD") + new Money(1m, "EUR");
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(12.3456)]
    [InlineData(0.0001)]
    [InlineData(1000000)]
    public void Proto_round_trip_keeps_value(double value)
    {
        var original = new Money((decimal)value, "USD");

        var back = original.ToProto().ToDomain();

        back.Should().Be(original);
    }

    [Fact]
    public void Proto_splits_units_and_nanos()
    {
        var proto = new Money(12.5m, "USD").ToProto();

        proto.Units.Should().Be(12);
        proto.Nanos.Should().Be(500_000_000);
        proto.Currency.Should().Be("USD");
    }
}