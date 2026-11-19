using Auction.Domain.Enums;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Lifecycle;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 05 tasks 4 to 6 without SQL or Redis: control answers, rebuild values, metrics, settle result.</summary>
public class LifecycleControlUnitTests
{
    [Theory]
    [InlineData(LifecycleResult.Done, LiveControlResult.Done)]
    [InlineData(LifecycleResult.Missing, LiveControlResult.NotInRedis)]
    [InlineData(LifecycleResult.InvalidState, LiveControlResult.InvalidState)]
    [InlineData(LifecycleResult.Skipped, LiveControlResult.InvalidState)]
    public void Control_script_answers_map_to_live_results(LifecycleResult script, LiveControlResult expected) =>
        RedisLiveAuctionControl.Map(script).Should().Be(expected);

    [Fact]
    public async Task Stub_says_not_in_redis_for_every_action()
    {
        var stub = new StubLiveAuctionControl();

        (await stub.CancelAsync(Guid.NewGuid(), force: true)).Result.Should().Be(LiveControlResult.NotInRedis);
        (await stub.CloseAsync(Guid.NewGuid())).Result.Should().Be(LiveControlResult.NotInRedis);
        (await stub.FreezeAsync(Guid.NewGuid())).Result.Should().Be(LiveControlResult.NotInRedis);
        (await stub.ResumeAsync(Guid.NewGuid())).Result.Should().Be(LiveControlResult.NotInRedis);
    }

    private static OpenAuctionRow Row(decimal? price, Guid? leader) => new()
    {
        AuctionId = Guid.NewGuid(), SellerId = Guid.NewGuid(), Currency = "USD", Status = (byte)AuctionStatus.Live,
        StartTime = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc),
        ScheduledEndTime = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc),
        EndTime = new DateTime(2026, 11, 20, 12, 2, 0, DateTimeKind.Utc),   // 2 minutes of anti-sniping used
        StartingPrice = 10m, IncrementTableId = 1, SnipeWindowSec = 60, ExtensionSec = 60, MaxTotalExtensionSec = 600,
        CurrentPrice = price, CurrentLeaderId = leader, BidCount = leader == null ? 0 : 4, LastSeq = 4, LastESeq = 6,
    };

    [Fact]
    public void Rebuild_values_come_from_sql()
    {
        var leader = Guid.NewGuid();
        var other = Guid.NewGuid();

        var r = Row(31.50m, leader).ToRebuild("0:100", new Dictionary<Guid, long> { [leader] = 9000, [other] = 3000 });

        r.PriceC.Should().Be(3150);
        r.LeaderId.Should().Be(leader);
        r.LeaderMaxC.Should().Be(9000);
        r.BidCount.Should().Be(4);
        r.Seq.Should().Be(4);
        r.ESeq.Should().Be(6);
        r.ExtUsedMs.Should().Be(120_000);
        r.Auction.EndMs.Should().Be(r.SchedEndMs + 120_000);
        r.MaxByUser.Should().ContainKey(other);
    }

    [Fact]
    public void Leader_max_is_at_least_the_price_when_bids_have_no_max()
    {
        var leader = Guid.NewGuid();

        Row(20m, leader).ToRebuild("0:100", new Dictionary<Guid, long>()).LeaderMaxC.Should().Be(2000);
    }

    [Fact]
    public void Auction_with_no_bids_rebuilds_with_price_0_and_no_leader()
    {
        var r = Row(null, null).ToRebuild("0:100", new Dictionary<Guid, long>());

        r.PriceC.Should().Be(0);
        r.LeaderId.Should().BeNull();
        r.LeaderMaxC.Should().Be(0);
    }

    [Fact]
    public void Settle_result_gives_final_price_in_cents() =>
        new SettleResult(AuctionOutcome.Sold, 123.45m, Guid.NewGuid(), Guid.NewGuid(), false).FinalC.Should().Be(12345);

    [Fact]
    public void Settlement_lag_metric_holds_the_last_value()
    {
        LifecycleMetrics.SetSettlementLag(42.5);
        LifecycleMetrics.SettlementLagSeconds.Should().Be(42.5);
        LifecycleMetrics.SetSettlementLag(0);
    }

    [Fact]
    public void Drain_warning_starts_after_30_seconds() =>
        LifecycleLoop.DrainWarnAfter.Should().Be(TimeSpan.FromSeconds(30));
}
