using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 05 task 6: recovery on leader start (R7). Uses the normal key layout so the admin resume
/// (which goes through RedisLiveAuctionControl) finds the rebuilt keys.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class LifecycleRecoveryTests(SqlServerFixture sql, RedisFixture redis)
{
    private IDatabase Redis => redis.Connection.GetDatabase();

    private LifecycleLoop Loop(LifecycleTestDb t) => new(
        redis.Connection,
        new LifecycleScripts(new ScriptRegistry(redis.Connection)),
        new LifecycleSql(t.Connections),
        new Settler(t.Connections, NullLogger<Settler>.Instance),
        RedisOptions.Default,
        new LifecycleOptions(),
        NullLogger<LifecycleLoop>.Instance);

    private static AuctionKeys KeysOf(Guid id) => RedisKeys.For(id, RedisOptions.Default);

    private static async Task AddBidAsync(LifecycleTestDb t, Guid auctionId, Guid user, long eseq, decimal amount, decimal max)
    {
        t.Db.Bids.Add(new Bid
        {
            AuctionId = auctionId, UserId = user, ESeq = eseq, Idx = 0, Seq = eseq, Kind = BidKind.Manual,
            Amount = amount, MaxAmount = max, IdempotencyKey = Guid.NewGuid(), AcceptedAt = DateTime.UtcNow,
        });
        await t.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Live_auction_lost_from_redis_is_rebuilt_frozen_with_the_right_price_and_leader()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "recover_rebuild");
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var a = await t.AddAuctionAsync(new(AuctionStatus.Live, CurrentPrice: 31m, Leader: bob, BidCount: 3, LastSeq: 3, LastESeq: 4));
        await AddBidAsync(t, a.AuctionId, alice, 1, 10m, 30m);
        await AddBidAsync(t, a.AuctionId, bob, 2, 31m, 45m);
        await AddBidAsync(t, a.AuctionId, bob, 3, 31m, 60m);   // bob raised his max later
        var keys = KeysOf(a.AuctionId);
        await Redis.KeyDeleteAsync([keys.State, keys.Max, keys.Bids, keys.Events]);   // Redis lost it

        var report = await Loop(t).RecoverAsync(t.Grant);

        report.Rebuilt.Should().Be(1);
        var state = (await Redis.HashGetAllAsync(keys.State)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        state["status"].Should().Be("Frozen");
        state["priceC"].Should().Be("3100");
        state["leader"].Should().Be(bob.ToString());
        state["leaderMaxC"].Should().Be("6000");
        state["bidCount"].Should().Be("3");
        state["persistedEseq"].Should().Be("4");
        ((long)await Redis.HashGetAsync(keys.Max, alice.ToString())).Should().Be(3000);
        await using var db = t.NewContext();
        (await db.Auctions.SingleAsync(x => x.AuctionId == a.AuctionId)).Status.Should().Be(AuctionStatus.Frozen);
    }

    [Fact]
    public async Task Admin_resume_works_after_a_rebuild()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "recover_resume");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Live, CurrentPrice: 10m, Leader: Guid.NewGuid(), BidCount: 1, LastSeq: 1, LastESeq: 1));
        var keys = KeysOf(a.AuctionId);
        await Redis.KeyDeleteAsync([keys.State, keys.Max, keys.Bids, keys.Events]);
        await Loop(t).RecoverAsync(t.Grant);

        var live = new RedisLiveAuctionControl(new LifecycleScripts(new ScriptRegistry(redis.Connection)), redis.Connection, RedisOptions.Default);
        await new AuctionControlManager(t.Db, live, new OutboxWriter(t.Db, TimeProvider.System), TimeProvider.System)
            .ResumeAsync(Guid.NewGuid(), a.AuctionId, "Checked after Redis loss", null);

        ((string?)await Redis.HashGetAsync(keys.State, "status")).Should().Be("Live");
        var bidder = Guid.NewGuid();
        var outcome = await new BidScript(new ScriptRegistry(redis.Connection))
            .RunAsync(keys, bidder, 5000, BidAmountRules.Fingerprint(bidder, 5000, "USD"), Guid.NewGuid(), "USD", "t");
        outcome.Accepted.Should().BeTrue("bidding works again after resume");
        await using var db = t.NewContext();
        (await db.Auctions.SingleAsync(x => x.AuctionId == a.AuctionId)).Status.Should().Be(AuctionStatus.Live);
    }

    [Fact]
    public async Task Live_auction_still_in_redis_is_put_back_in_the_index()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "recover_index");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Live));
        var keys = await LiveAuctionSeeder.SeedAsync(Redis, new(a.AuctionId, LifecycleTestDb.Seller, EndsInMs: 600_000));
        var end = (long)await Redis.HashGetAsync(keys.State, "endMs");
        await Redis.SortedSetRemoveAsync(keys.Index.Ends, a.AuctionId.ToString());

        var report = await Loop(t).RecoverAsync(t.Grant);

        report.Indexed.Should().BeGreaterThanOrEqualTo(1);
        (await Redis.SortedSetScoreAsync(keys.Index.Ends, a.AuctionId.ToString())).Should().Be(end);
        (await Redis.SortedSetScoreAsync(keys.Index.Soon, a.AuctionId.ToString())).Should().Be(end - 60_000);
        ((string?)await Redis.HashGetAsync(keys.State, "status")).Should().Be("Live", "nothing else changes");

        await Redis.SortedSetRemoveAsync(keys.Index.Ends, a.AuctionId.ToString());   // keep {p0} clean
        await Redis.SortedSetRemoveAsync(keys.Index.Soon, a.AuctionId.ToString());
    }

    [Fact]
    public async Task Sealed_auction_lost_from_redis_is_settled_from_sql()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "recover_sealed");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Sealed, CurrentPrice: 80m, Leader: Guid.NewGuid(), BidCount: 2));
        var keys = KeysOf(a.AuctionId);
        await Redis.KeyDeleteAsync(keys.State);

        var report = await Loop(t).RecoverAsync(t.Grant);

        report.SettledFromSql.Should().Be(1);
        await using var db = t.NewContext();
        (await db.Auctions.SingleAsync(x => x.AuctionId == a.AuctionId)).Status.Should().Be(AuctionStatus.Settled);
        (await db.Settlements.CountAsync(s => s.AuctionId == a.AuctionId)).Should().Be(1);
    }
}
