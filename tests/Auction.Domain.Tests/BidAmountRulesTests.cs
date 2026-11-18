using Auction.Domain.Bidding;
using Auction.Domain.Errors;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 03 task 6: the max bid a user types (R3.4) and the idempotency fingerprint (R6.2).</summary>
public class BidAmountRulesTests
{
    private static FieldIssue Issue(long units, int nanos, string? currency = "USD")
    {
        var act = () => BidAmountRules.ToCents(units, nanos, currency);
        var ex = act.Should().Throw<DomainException>().Which;
        ex.Code.Should().Be(ErrorCodes.ValidationFailed);
        return ex.Fields.Should().ContainSingle().Which;
    }

    [Theory]
    [InlineData(10, 0, 1000)]
    [InlineData(10, 500_000_000, 1050)]          // 10.50
    [InlineData(10, 10_000_000, 1001)]           // 10.01
    [InlineData(0, 10_000_000, 1)]               // 0.01, the smallest bid
    [InlineData(10_000_000_000, 0, 1_000_000_000_000)]
    public void Valid_amounts_become_cents(long units, int nanos, long cents)
    {
        BidAmountRules.ToCents(units, nanos, "USD").Should().Be(cents);
    }

    [Fact]
    public void Zero_is_refused()
    {
        Issue(0, 0).Rule.Should().Be("min");
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, -10_000_000)]
    [InlineData(-1, -500_000_000)]
    public void Negative_is_refused(long units, int nanos)
    {
        Issue(units, nanos).Rule.Should().Be("min");
    }

    [Theory]
    [InlineData(10, 1_000_000)]     // 10.001
    [InlineData(10, 5_000_000)]     // 10.005
    [InlineData(0, 1)]              // 0.000000001
    public void More_than_2_decimals_is_refused(long units, int nanos)
    {
        Issue(units, nanos).Rule.Should().Be("decimals");
    }

    [Theory]
    [InlineData(1, -500_000_000)]       // signs disagree
    [InlineData(-1, 500_000_000)]
    [InlineData(1, 1_000_000_000)]      // nanos must be under one unit
    [InlineData(1, -1_000_000_000)]
    public void Broken_money_values_are_refused(long units, int nanos)
    {
        Issue(units, nanos).Rule.Should().Be("format");
    }

    [Fact]
    public void Too_large_is_refused()
    {
        Issue(BidAmountRules.MaxUnits + 1, 0).Rule.Should().Be("max");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("DOLLAR")]
    public void Bad_currency_is_refused(string? currency)
    {
        Issue(10, 0, currency).Field.Should().Be("max_amount.currency");
    }

    [Fact]
    public void Bad_currency_and_bad_amount_are_both_reported()
    {
        var act = () => BidAmountRules.ToCents(0, 0, "");

        act.Should().Throw<DomainException>().Which.Fields.Should().HaveCount(2);
    }

    [Fact]
    public void Fingerprint_is_user_amount_and_upper_case_currency()
    {
        var user = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

        BidAmountRules.Fingerprint(user, 1050, " usd ").Should().Be($"{user}:1050:USD");
    }

    [Fact]
    public void Different_amounts_give_different_fingerprints()
    {
        var user = Guid.NewGuid();

        BidAmountRules.Fingerprint(user, 1000, "USD").Should().NotBe(BidAmountRules.Fingerprint(user, 1001, "USD"));
        BidAmountRules.Fingerprint(user, 1000, "USD").Should().NotBe(BidAmountRules.Fingerprint(Guid.NewGuid(), 1000, "USD"));
    }
}
