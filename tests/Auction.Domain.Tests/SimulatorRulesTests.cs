using Auction.Domain.Bidding;
using Auction.Domain.Errors;
using Auction.Simulator.Sim;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 08 tasks 3 to 5 without any service: rate shapes, token bucket, bid choice, latency numbers, run settings.</summary>
public class SimulatorRulesTests
{
    private const long End = 10_000_000;

    // ---------- scenarios (R2.1 to R2.3) ----------

    [Fact]
    public void Steady_is_the_same_rate_all_the_way()
    {
        Scenarios.RateFor(Scenarios.Steady, 5, End - 600_000, End, 60).Should().Be(5);
        Scenarios.RateFor(Scenarios.Steady, 5, End - 1000, End, 60).Should().Be(5);
    }

    [Fact]
    public void Closing_spike_is_10_percent_until_the_last_60_seconds_then_full()
    {
        Scenarios.RateFor(Scenarios.ClosingSpike, 10, End - 120_000, End, 60).Should().Be(1);
        Scenarios.RateFor(Scenarios.ClosingSpike, 10, End - 60_001, End, 60).Should().Be(1);
        Scenarios.RateFor(Scenarios.ClosingSpike, 10, End - 60_000, End, 60).Should().Be(10);
        Scenarios.RateFor(Scenarios.ClosingSpike, 10, End - 1000, End, 60).Should().Be(10);
    }

    [Fact]
    public void Snipe_war_is_5_percent_until_the_snipe_window_then_full_and_follows_a_moving_end()
    {
        Scenarios.RateFor(Scenarios.SnipeWar, 10, End - 31_000, End, 30).Should().Be(0.5);
        Scenarios.RateFor(Scenarios.SnipeWar, 10, End - 30_000, End, 30).Should().Be(10);

        // An extension moves the end 15 s later: the same moment is quiet again, then full again.
        Scenarios.RateFor(Scenarios.SnipeWar, 10, End - 10_000, End + 15_000, 30).Should().Be(10);
        Scenarios.RateFor(Scenarios.SnipeWar, 10, End + 1000, End + 40_000, 30).Should().Be(0.5);
    }

    [Theory]
    [InlineData(Scenarios.Steady)]
    [InlineData(Scenarios.ClosingSpike)]
    [InlineData(Scenarios.SnipeWar)]
    public void No_bids_after_the_end(string scenario) => Scenarios.RateFor(scenario, 10, End, End, 60).Should().Be(0);

    [Fact]
    public void Scenario_names_and_which_make_their_own_auctions()
    {
        Scenarios.IsKnown("steady").Should().BeTrue();
        Scenarios.IsKnown("chaos").Should().BeFalse();
        Scenarios.MakesAuctions(Scenarios.Steady).Should().BeFalse();
        Scenarios.MakesAuctions(Scenarios.ClosingSpike).Should().BeTrue();
        Scenarios.MakesAuctions(Scenarios.SnipeWar).Should().BeTrue();
    }

    // ---------- token bucket ----------

    [Fact]
    public void Bucket_gives_rate_times_time_and_keeps_the_fraction()
    {
        var bucket = new TokenBucket();

        bucket.Take(10, TimeSpan.FromMilliseconds(100)).Should().Be(1);
        bucket.Take(2.5, TimeSpan.FromMilliseconds(100)).Should().Be(0, "0.25 of a bid is not enough yet");
        bucket.Take(2.5, TimeSpan.FromMilliseconds(100)).Should().Be(0);
        bucket.Take(2.5, TimeSpan.FromMilliseconds(200)).Should().Be(1, "the pieces add up");
    }

    [Fact]
    public void Bucket_never_bursts_past_its_limit() =>
        new TokenBucket().Take(1000, TimeSpan.FromSeconds(10), burst: 50).Should().Be(50);

    // ---------- bid choice (R2.4, R2.5) ----------

    private static readonly IncrementTable Table = IncrementTable.Parse("0:100,5000:250");

    [Fact]
    public void A_bot_that_leads_does_not_bid() =>
        BidChooser.Choose(1100, 1000, Table, 2, 1_000_000, botLeads: true, new Random(1)).Should().BeNull();

    [Fact]
    public void Bids_are_the_minimum_plus_0_to_aggressiveness_times_5_increments()
    {
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var max = BidChooser.Choose(1100, 1000, Table, aggressiveness: 2, ceilingC: 10_000_000, botLeads: false, random)!.Value;

            max.Should().BeInRange(1100, 1100 + (10 * 100));
            ((max - 1100) % 100).Should().Be(0, "whole increments");
        }
    }

    [Fact]
    public void At_the_ceiling_only_the_minimum_fits()
    {
        var random = new Random(3);
        var results = Enumerable.Range(0, 200).Select(_ => BidChooser.Choose(5000, 4900, Table, 5, ceilingC: 5000, botLeads: false, random)).ToList();

        results.Where(r => r != null).Should().OnlyContain(r => r == 5000);
        results.Should().Contain((long?)5000).And.Contain((long?)null);
    }

    [Fact]
    public void Past_the_ceiling_the_bot_gives_up() =>
        BidChooser.Choose(5001, 4900, Table, 5, ceilingC: 5000, botLeads: false, new Random(3)).Should().BeNull();

    [Fact]
    public void The_same_seed_makes_the_same_choices()
    {
        List<long?> Run(int seed)
        {
            var random = new Random(seed);
            return Enumerable.Range(0, 50).Select(_ => BidChooser.Choose(1100, 1000, Table, 3, 100_000, false, random)).ToList();
        }

        Run(42).Should().Equal(Run(42));
        Run(42).Should().NotEqual(Run(43));
    }

    [Fact]
    public void Ceiling_is_3_to_20_times_the_starting_price()
    {
        var random = new Random(5);
        for (var i = 0; i < 200; i++)
        {
            BidChooser.Ceiling(1000, random).Should().BeInRange(3000, 20_000);
        }
    }

    // ---------- latency numbers ----------

    [Fact]
    public void Percentiles_of_1_to_100()
    {
        var r = new LatencyReservoir();
        for (var i = 1; i <= 100; i++)
        {
            r.Add(i);
        }

        r.Percentile(50).Should().Be(50);
        r.Percentile(99).Should().Be(99);
        r.Percentile(100).Should().Be(100);
    }

    [Fact]
    public void No_samples_give_0() => new LatencyReservoir().Percentile(99).Should().Be(0);

    [Fact]
    public void Only_the_newest_20000_count()
    {
        var r = new LatencyReservoir();
        for (var i = 0; i < 20_000; i++)
        {
            r.Add(1000);
        }

        for (var i = 0; i < 20_000; i++)
        {
            r.Add(5);
        }

        r.Percentile(99).Should().Be(5);
    }

    // ---------- run settings (R3.1) ----------

    private static RunConfig Config(string scenario = "steady", int bots = 10, double rate = 2, int aggr = 2, int demoAuctions = 5, int demoMinutes = 3, int duration = 60) =>
        new(scenario, bots, rate, [], demoAuctions, demoMinutes, duration, aggr, 0);

    [Fact]
    public void Good_settings_pass() => RunManager.Validate(Config());

    [Theory]
    [InlineData("chaos", 10, 2, "scenario")]
    [InlineData("steady", 0, 2, "bots")]
    [InlineData("steady", 501, 2, "bots")]
    [InlineData("steady", 10, 0, "rate_per_auction")]
    [InlineData("steady", 10, 51, "rate_per_auction")]
    public void Bad_settings_name_the_field(string scenario, int bots, double rate, string field)
    {
        var validate = () => RunManager.Validate(Config(scenario, bots, rate));

        var ex = validate.Should().Throw<DomainException>().Which;
        ex.Code.Should().Be(ErrorCodes.ValidationFailed);
        ex.Fields.Select(f => f.Field).Should().Contain(field);
    }

    [Fact]
    public void Each_scenario_checks_its_own_fields()
    {
        var steady = () => RunManager.Validate(Config(duration: 1));
        var spike = () => RunManager.Validate(Config(Scenarios.ClosingSpike, demoAuctions: 0));
        var spikeOk = () => RunManager.Validate(Config(Scenarios.ClosingSpike, duration: 1));

        steady.Should().Throw<DomainException>().Which.Fields.Select(f => f.Field).Should().Contain("duration_sec");
        spike.Should().Throw<DomainException>().Which.Fields.Select(f => f.Field).Should().Contain("demo_auctions");
        spikeOk.Should().NotThrow("closing-spike ends with its auctions, not by duration");
    }
}
