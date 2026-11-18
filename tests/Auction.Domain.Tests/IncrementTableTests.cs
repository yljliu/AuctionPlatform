using Auction.Domain.Bidding;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 03 task 1: increment tiers, min next bid, Redis string form.</summary>
public class IncrementTableTests
{
    // Seed table 1: 0 -> 1.00, 50 -> 2.50, 250 -> 5.00, 1000 -> 10.00, 5000 -> 50.00
    private static readonly IncrementTable Table1 = IncrementTable.FromMoney(
    [
        (250m, 5m), (0m, 1m), (5000m, 50m), (50m, 2.5m), (1000m, 10m),   // out of order on purpose
    ]);

    private const string Table1Redis = "0:100,5000:250,25000:500,100000:1000,500000:5000";

    // ---------- tier boundaries (R3.2) ----------

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 100)]
    [InlineData(4999, 100)]      // 49.99: last cent of tier 1
    [InlineData(5000, 250)]      // 50.00: first cent of tier 2
    [InlineData(24999, 250)]
    [InlineData(25000, 500)]
    [InlineData(99999, 500)]
    [InlineData(100000, 1000)]
    [InlineData(499999, 1000)]
    [InlineData(500000, 5000)]
    [InlineData(999_999_999, 5000)]
    public void Increment_uses_the_tier_at_or_below_the_price(long priceC, long expectedC)
    {
        Table1.IncrementFor(priceC).Should().Be(expectedC);
    }

    [Fact]
    public void Negative_price_uses_the_first_tier()
    {
        Table1.IncrementFor(-1).Should().Be(100);
    }

    // ---------- min next (R3.1, R3.2) ----------

    [Fact]
    public void Min_next_is_the_starting_price_when_there_are_no_bids()
    {
        Table1.MinNext(priceC: 0, bidCount: 0, startC: 2500).Should().Be(2500);
    }

    [Fact]
    public void Min_next_ignores_the_price_when_there_are_no_bids()
    {
        Table1.MinNext(priceC: 9000, bidCount: 0, startC: 2500).Should().Be(2500);
    }

    [Theory]
    [InlineData(4000, 4100)]     // 40.00 + 1.00
    [InlineData(4999, 5099)]     // 49.99 is still tier 1
    [InlineData(5000, 5250)]     // 50.00 + 2.50
    [InlineData(25000, 25500)]   // 250.00 + 5.00
    [InlineData(100000, 101000)] // 1000.00 + 10.00
    [InlineData(500000, 505000)] // 5000.00 + 50.00
    public void Min_next_is_price_plus_increment_after_a_bid(long priceC, long expectedC)
    {
        Table1.MinNext(priceC, bidCount: 3, startC: 2500).Should().Be(expectedC);
    }

    [Fact]
    public void Single_tier_table_always_uses_that_increment()
    {
        var flat = new IncrementTable([(0, 50)]);

        flat.IncrementFor(0).Should().Be(50);
        flat.IncrementFor(1_000_000).Should().Be(50);
    }

    // ---------- loading from money rows ----------

    [Fact]
    public void From_money_sorts_and_converts_to_cents()
    {
        Table1.Tiers.Should().Equal((0L, 100L), (5000L, 250L), (25000L, 500L), (100000L, 1000L), (500000L, 5000L));
    }

    [Fact]
    public void From_money_refuses_fractions_of_a_cent()
    {
        var act = () => IncrementTable.FromMoney([(0m, 2.505m)]);

        act.Should().Throw<ArgumentException>().WithMessage("*whole number of cents*");
    }

    // ---------- table rules ----------

    [Fact]
    public void Empty_table_is_refused()
    {
        var act = () => new IncrementTable([]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void First_tier_must_start_at_zero()
    {
        var act = () => new IncrementTable([(100, 100)]);
        act.Should().Throw<ArgumentException>().WithMessage("*start at 0*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Increment_must_be_above_zero(long incC)
    {
        var act = () => new IncrementTable([(0, 100), (5000, incC)]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Duplicate_start_is_refused()
    {
        var act = () => new IncrementTable([(0, 100), (5000, 250), (5000, 500)]);
        act.Should().Throw<ArgumentException>().WithMessage("*strictly up*");
    }

    [Fact]
    public void Table_keeps_its_own_copy_of_the_tiers()
    {
        var list = new List<(long, long)> { (0, 100) };
        var table = new IncrementTable(list);

        list.Add((5000, 250));

        table.Tiers.Should().HaveCount(1);
    }

    // ---------- Redis "inc" string ----------

    [Fact]
    public void Redis_string_matches_the_design_example()
    {
        Table1.ToRedisString().Should().Be(Table1Redis);
    }

    [Fact]
    public void Redis_string_round_trips()
    {
        var back = IncrementTable.Parse(Table1Redis);

        back.Tiers.Should().Equal(Table1.Tiers);
        back.ToRedisString().Should().Be(Table1Redis);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("0:100,")]
    [InlineData("0-100")]
    [InlineData("0:100:5")]
    [InlineData("0:abc")]
    [InlineData("0:-100")]
    [InlineData("0:1.5")]
    [InlineData("100:100")]          // does not start at 0
    [InlineData("0:100,0:200")]      // start not going up
    public void Bad_redis_string_is_a_format_error(string? text)
    {
        var act = () => IncrementTable.Parse(text);
        act.Should().Throw<FormatException>();
    }
}
