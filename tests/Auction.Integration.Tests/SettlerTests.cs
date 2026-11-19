using Auction.Domain.Enums;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 05 task 4: the settlement transaction (R5.3 to R5.7) and step E, which waits for the
/// persister to drain (R5.1, R5.2). Redis keys live in partition {p963}.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class SettlerTests(SqlServerFixture sql, RedisFixture redis)
{
    private const int Partition = 963;

    private IDatabase Redis => redis.Connection.GetDatabase();

    private static Settler Settler(LifecycleTestDb t) => new(t.Connections, NullLogger<Settler>.Instance);

    [Fact]
    public async Task No_bids_is_unsold_with_no_settlement()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "settle_nobids");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Sealed));

        var result = await Settler(t).SettleAsync(t.Grant, a.AuctionId);

        result!.Outcome.Should().Be(AuctionOutcome.Unsold);
        await using var db = t.NewContext();
        (await db.Settlements.CountAsync(s => s.AuctionId == a.AuctionId)).Should().Be(0);
        var after = await db.Auctions.SingleAsync(x => x.AuctionId == a.AuctionId);
        after.Status.Should().Be(AuctionStatus.Settled);
        after.Outcome.Should().Be(AuctionOutcome.Unsold);
        (await db.Items.SingleAsync(i => i.ItemId == a.ItemId)).Status.Should().Be(ItemStatus.Unsold);
        (await db.Outbox.CountAsync(o => o.Topic == "AuctionSettled" && o.Key == a.AuctionId.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task Below_reserve_is_unsold()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "settle_reserve");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Sealed, CurrentPrice: 90m, Leader: Guid.NewGuid(), BidCount: 3, Reserve: 100m));

        var result = await Settler(t).SettleAsync(t.Grant, a.AuctionId);

        result!.Outcome.Should().Be(AuctionOutcome.Unsold);
        result.WinnerId.Should().BeNull();
        await using var db = t.NewContext();
        (await db.Settlements.CountAsync(s => s.AuctionId == a.AuctionId)).Should().Be(0);
    }

    [Fact]
    public async Task Sold_has_the_right_totals()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "settle_sold");
        var winner = Guid.NewGuid();
        var a = await t.AddAuctionAsync(new(AuctionStatus.Sealed, CurrentPrice: 250m, Leader: winner, BidCount: 4, Reserve: 200m,
            Shipping: ShippingType.BuyerPays, ShippingCost: 15m));

        var result = await Settler(t).SettleAsync(t.Grant, a.AuctionId);

        result!.Outcome.Should().Be(AuctionOutcome.Sold);
        result.WinnerId.Should().Be(winner);
        result.FinalC.Should().Be(25_000);
        await using var db = t.NewContext();
        var s = await db.Settlements.SingleAsync(x => x.AuctionId == a.AuctionId);
        s.BuyerId.Should().Be(winner);
        s.SellerId.Should().Be(LifecycleTestDb.Seller);
        s.HammerPrice.Should().Be(250m);
        s.BuyerPremium.Should().Be(12.50m, "5 % of 250");
        s.ShippingCost.Should().Be(15m);
        s.Tax.Should().Be(0m);
        s.TotalDue.Should().Be(277.50m);
        s.SellerFee.Should().Be(20m, "8 % of 250");
        s.Status.Should().Be(SettlementStatus.PendingPayment);
        s.PaymentDeadline.Should().BeCloseTo(DateTime.UtcNow.AddHours(72), TimeSpan.FromMinutes(2));
        (await db.Items.SingleAsync(i => i.ItemId == a.ItemId)).Status.Should().Be(ItemStatus.Sold);
    }

    [Fact]
    public async Task Rerun_after_a_crash_creates_no_second_settlement()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "settle_rerun");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Sealed, CurrentPrice: 100m, Leader: Guid.NewGuid(), BidCount: 2));
        var first = await Settler(t).SettleAsync(t.Grant, a.AuctionId);

        // As if the process died after the SQL commit and before settle.lua: the next tick settles again.
        var again = await Settler(t).SettleAsync(t.Grant, a.AuctionId);

        again!.AlreadySettled.Should().BeTrue();
        again.SettlementId.Should().Be(first!.SettlementId);
        again.Outcome.Should().Be(AuctionOutcome.Sold);
        again.FinalPrice.Should().Be(100m);
        await using var db = t.NewContext();
        (await db.Settlements.CountAsync(s => s.AuctionId == a.AuctionId)).Should().Be(1);
        (await db.Outbox.CountAsync(o => o.Topic == "AuctionSettled" && o.Key == a.AuctionId.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task Stale_token_cannot_settle()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "settle_stale");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Sealed));
        var stale = t.Grant with { Token = t.Grant.Token + 5 };

        var settle = () => Settler(t).SettleAsync(stale, a.AuctionId);

        await settle.Should().ThrowAsync<StaleFencingTokenException>();
        await using var db = t.NewContext();
        (await db.Auctions.SingleAsync(x => x.AuctionId == a.AuctionId)).Status.Should().Be(AuctionStatus.Sealed);
    }

    [Fact]
    public async Task Cancelled_auction_is_not_settled()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "settle_cancelled");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Cancelled));

        (await Settler(t).SettleAsync(t.Grant, a.AuctionId)).Should().BeNull();
    }

    [Fact]
    public async Task Step_E_waits_until_the_persister_has_drained_then_settles()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "settle_drain");
        var winner = Guid.NewGuid();
        var a = await t.AddAuctionAsync(new(AuctionStatus.Sealed, CurrentPrice: 40m, Leader: winner, BidCount: 1));
        var keys = await LiveAuctionSeeder.SeedAsync(Redis, new(a.AuctionId, LifecycleTestDb.Seller, EndsInMs: -1), Partition);
        var scripts = new LifecycleScripts(new ScriptRegistry(redis.Connection));
        await scripts.SealAsync(keys);
        await Redis.HashSetAsync(keys.State, [new("eseq", 5), new("persistedEseq", 3)]);   // 2 entries not in SQL yet
        var loop = new LifecycleLoop(redis.Connection, scripts, new LifecycleSql(t.Connections), Settler(t), RedisOptions.Default,
            new LifecycleOptions { OnlyPartitions = [Partition], KeysFor = id => new AuctionKeys(id, Partition) },
            NullLogger<LifecycleLoop>.Instance);

        (await loop.TickAsync(t.Grant)).Settled.Should().Be(0, "settling waits for the persister (R5.1)");
        ((string?)await Redis.HashGetAsync(keys.State, "status")).Should().Be("Sealed", "it never reopens (R5.2)");

        await Redis.HashSetAsync(keys.State, "persistedEseq", 5);
        (await loop.TickAsync(t.Grant)).Settled.Should().Be(1);

        ((string?)await Redis.HashGetAsync(keys.State, "status")).Should().Be("Settled");
        ((string?)await Redis.HashGetAsync(keys.State, "winner")).Should().Be(winner.ToString());
        ((long)await Redis.HashGetAsync(keys.State, "finalC")).Should().Be(4000);
        await using var db = t.NewContext();
        (await db.Auctions.SingleAsync(x => x.AuctionId == a.AuctionId)).Status.Should().Be(AuctionStatus.Settled);
    }

    [Fact]
    public async Task Long_drain_raises_the_settlement_lag_metric()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "settle_lag");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Sealed));
        var keys = await LiveAuctionSeeder.SeedAsync(Redis, new(a.AuctionId, LifecycleTestDb.Seller, EndsInMs: -1), Partition);
        var scripts = new LifecycleScripts(new ScriptRegistry(redis.Connection));
        await scripts.SealAsync(keys);
        var longAgo = DateTimeOffset.UtcNow.AddSeconds(-45).ToUnixTimeMilliseconds();
        await Redis.HashSetAsync(keys.State, [new("eseq", 9), new("persistedEseq", 1), new("sealedAtMs", longAgo)]);
        var loop = new LifecycleLoop(redis.Connection, scripts, new LifecycleSql(t.Connections), Settler(t), RedisOptions.Default,
            new LifecycleOptions { OnlyPartitions = [Partition], KeysFor = id => new AuctionKeys(id, Partition) },
            NullLogger<LifecycleLoop>.Instance);

        await loop.TickAsync(t.Grant);

        LifecycleMetrics.SettlementLagSeconds.Should().BeGreaterThan(40);
        await Redis.SortedSetRemoveAsync(keys.Index.Sealed, a.AuctionId.ToString());   // leave the partition clean
    }
}
