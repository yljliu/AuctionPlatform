using Auction.Domain.Bidding;
using Auction.Domain.Identity;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

public class IncrementTableAndHandleTests
{
    // Seed table 1: 0 -> 1.00, 50 -> 2.50, 250 -> 5.00, 1000 -> 10.00, 5000 -> 50.00
    private static readonly IncrementTable Table1 = IncrementTable.FromMoney(
    [
        (250m, 5m), (0m, 1m), (5000m, 50m), (50m, 2.5m), (1000m, 10m),   // out of order on purpose
    ]);

    [Theory]
    [InlineData(0, 100)]
    [InlineData(4999, 100)]
    [InlineData(5000, 250)]
    [InlineData(24999, 250)]
    [InlineData(25000, 500)]
    [InlineData(100000, 1000)]
    [InlineData(500000, 5000)]
    [InlineData(9999999, 5000)]
    public void Increment_uses_the_tier_at_or_below_the_price(long priceC, long expectedC)
    {
        Table1.IncrementFor(priceC).Should().Be(expectedC);
    }

    [Fact]
    public void Min_next_is_the_starting_price_when_there_are_no_bids()
    {
        Table1.MinNext(priceC: 0, bidCount: 0, startC: 2500).Should().Be(2500);
    }

    [Fact]
    public void Min_next_is_price_plus_increment_after_a_bid()
    {
        Table1.MinNext(priceC: 4000, bidCount: 2, startC: 2500).Should().Be(4100);
        Table1.MinNext(priceC: 5000, bidCount: 2, startC: 2500).Should().Be(5250);
    }

    [Fact]
    public void Empty_table_throws()
    {
        var act = () => new IncrementTable([]).IncrementFor(100);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("bobsmith42", "b***42")]
    [InlineData("alice", "a***ce")]
    [InlineData("abcd", "a***cd")]
    [InlineData("abc", "a***")]
    [InlineData("x", "x***")]
    [InlineData("", "***")]
    [InlineData(null, "***")]
    public void Handles_are_masked(string? username, string expected)
    {
        HandleMask.Mask(username).Should().Be(expected);
    }
}