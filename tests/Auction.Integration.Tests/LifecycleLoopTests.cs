using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 05 task 3: the real lifecycle loop (steps A to D) ticking every 250 ms against SQL and Redis.
/// It runs only partition {p961}, so a workers copy on the same Redis never touches these auctions.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class LifecycleLoopTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
{
    private const int Partition = 961;
    private static readonly Guid Seller = Guid.Parse("55555555-0000-0000-0000-000000000961");

    private AuctionDbContext _db = null!;
    private SqlConnectionFactory _connections = null!;
    private int _categoryId;
    private LeaseManager _lease = null!;

    private IDatabase Redis => redis.Connection.GetDatabase();

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("lifecycle_loop");
        await _db.Database.MigrateAsync();
        _connections = new SqlConnectionFactory(sql.ConnectionStringFor("lifecycle_loop"));

        var category = new Category { Name = "c", Slug = "c" };
        _db.Categories.Add(category);
        foreach (var (from, inc) in new[] { (0m, 1m), (50m, 2.5m), (250m, 5m) })
        {
            _db.BidIncrementTiers.Add(new BidIncrementTier { TableId = 1, FromPrice = from, Increment = inc });
        }

        await _db.SaveChangesAsync();
        _categoryId = category.CategoryId;

        _lease = new LeaseManager(new SqlLeaseStore(_connections), new LeaseOptions { Holder = "loop-test" },
            TimeProvider.System, NullLogger<LeaseManager>.Instance);
        (await _lease.RenewOnceAsync()).Should().BeTrue();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private LifecycleLoop Loop() => new(
        redis.Connection,
        new LifecycleScripts(new ScriptRegistry(redis.Connection)),
        new LifecycleSql(_connections),
        new Settler(_connections, NullLogger<Settler>.Instance),
        RedisOptions.Default,
        new LifecycleOptions { OnlyPartitions = [Partition], KeysFor = id => new AuctionKeys(id, Partition) },
        NullLogger<LifecycleLoop>.Instance);

    /// <summary>A Scheduled auction in SQL only, starting and ending this many ms from now.</summary>
    private async Task<AuctionKeys> ScheduledAsync(long startsInMs, long endsInMs, int snipeSec = 0, int extSec = 0, int maxExtSec = 0)
    {
        var now = DateTime.UtcNow;
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = Seller, CategoryId = _categoryId, Title = "t", Description = "d",
            ShippingType = ShippingType.FreeShipping, Status = ItemStatus.InAuction, CreatedAt = now, UpdatedAt = now,
        };
        var auction = new AuctionEntity
        {
            AuctionId = Guid.NewGuid(), ItemId = item.ItemId, SellerId = Seller,
            StartTime = now.AddMilliseconds(startsInMs), ScheduledEndTime = now.AddMilliseconds(endsInMs), EndTime = now.AddMilliseconds(endsInMs),
            StartingPrice = 10, FeeScheduleId = 1, IncrementTableId = 1,
            SnipeWindowSec = snipeSec, ExtensionSec = extSec, MaxTotalExtensionSec = maxExtSec,
            Status = AuctionStatus.Scheduled, CreatedAt = now, UpdatedAt = now,
        };
        _db.Items.Add(item);
        _db.Auctions.Add(auction);
        await _db.SaveChangesAsync();
        return new AuctionKeys(auction.AuctionId, Partition);
    }

    /// <summary>Ticks the loop every 250 ms in the background until disposed.</summary>
    private sealed class Ticker : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;

        public Ticker(LifecycleLoop loop, LeaseGrant grant) => _run = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                await loop.TickAsync(grant);
                await Task.Delay(250);
            }
        });

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _run;
        }
    }

    private async Task<AuctionStatus> SqlStatusAsync(Guid id)
    {
        await using var db = sql.CreateContext("lifecycle_loop");
        return await db.Auctions.Where(a => a.AuctionId == id).Select(a => a.Status).SingleAsync();
    }

    private async Task WaitForRedisStatusAsync(AuctionKeys keys, string status, int timeoutMs)
    {
        var deadline = NowMs + timeoutMs;
        while ((string?)await Redis.HashGetAsync(keys.State, "status") != status)
        {
            NowMs.Should().BeLessThan(deadline, $"auction should reach {status} in time");
            await Task.Delay(50);
        }
    }

    /// <summary>The "at" (Redis time) of this auction's State entry with the given change.</summary>
    private async Task<long> EventAtAsync(AuctionKeys keys, string change)
    {
        var entry = (await Redis.StreamRangeAsync(keys.Stream))
            .Single(e => e["auction"] == keys.AuctionId.ToString() && e["change"] == change);
        return (long)entry["at"];
    }

    private async Task<string[]> ChangesAsync(AuctionKeys keys) =>
        (await Redis.StreamRangeAsync(keys.Stream))
            .Where(e => e["auction"] == keys.AuctionId.ToString() && e["type"] == "State")
            .Select(e => e["change"].ToString())
            .ToArray();

    [Fact]
    public async Task Auction_starting_in_2_s_opens_within_1_s_then_seals_within_1_s_of_its_end()
    {
        var keys = await ScheduledAsync(startsInMs: 2000, endsInMs: 5000);
        var startMs = NowMs + 2000;

        await using (new Ticker(Loop(), _lease.Current!))
        {
            await WaitForRedisStatusAsync(keys, "Live", 6000);
            await WaitForRedisStatusAsync(keys, "Sealed", 8000);
        }

        var start = (long)await Redis.HashGetAsync(keys.State, "startMs");
        var end = (long)await Redis.HashGetAsync(keys.State, "endMs");
        start.Should().BeCloseTo(startMs, 1000);
        (await EventAtAsync(keys, "Opened") - start).Should().BeInRange(0, 1000, "opened within 1 s of the start (R2.2)");
        (await EventAtAsync(keys, "Sealed") - end).Should().BeInRange(0, 1000, "sealed within 1 s of the end (R4.1)");
        (await ChangesAsync(keys)).Should().Equal("Opened", "ClosingSoon", "Sealed");
        (await SqlStatusAsync(keys.AuctionId)).Should().Be(AuctionStatus.Sealed);
    }

    [Fact]
    public async Task Load_step_puts_soon_starting_auctions_in_redis_but_not_later_ones()
    {
        var soon = await ScheduledAsync(startsInMs: 60_000, endsInMs: 600_000);
        var later = await ScheduledAsync(startsInMs: 3_600_000, endsInMs: 7_200_000);

        var report = await Loop().TickAsync(_lease.Current!);

        report.Loaded.Should().BeGreaterThanOrEqualTo(1);
        ((string?)await Redis.HashGetAsync(soon.State, "status")).Should().Be("Scheduled");
        ((string?)await Redis.HashGetAsync(soon.State, "inc")).Should().Be("0:100,5000:250,25000:500");
        (await Redis.KeyExistsAsync(later.State)).Should().BeFalse("it starts in an hour, past the 5 minute window");
        (await SqlStatusAsync(soon.AuctionId)).Should().Be(AuctionStatus.Scheduled);
    }

    [Fact]
    public async Task Late_bid_extension_delays_the_seal()
    {
        // 3 s anti-sniping window, 3 s extension.
        var keys = await ScheduledAsync(startsInMs: 500, endsInMs: 3000, snipeSec: 3, extSec: 3, maxExtSec: 30);

        await using (new Ticker(Loop(), _lease.Current!))
        {
            await WaitForRedisStatusAsync(keys, "Live", 4000);
            var firstEnd = (long)await Redis.HashGetAsync(keys.State, "endMs");

            var bidder = Guid.NewGuid();
            var outcome = await new BidScript(new ScriptRegistry(redis.Connection))
                .RunAsync(keys, bidder, 5000, BidAmountRules.Fingerprint(bidder, 5000, "USD"), Guid.NewGuid(), "USD", "t");
            outcome.Accepted.Should().BeTrue();
            outcome.Extended.Should().BeTrue();
            var newEnd = (long)await Redis.HashGetAsync(keys.State, "endMs");
            newEnd.Should().Be(firstEnd + 3000);

            // Past the first end, still Live.
            while (NowMs < firstEnd + 600)
            {
                await Task.Delay(100);
            }

            ((string?)await Redis.HashGetAsync(keys.State, "status")).Should().Be("Live", "the bid moved the end later (R4.2)");

            await WaitForRedisStatusAsync(keys, "Sealed", 6000);
            (await EventAtAsync(keys, "Sealed")).Should().BeGreaterThanOrEqualTo(newEnd);
        }
    }

    [Fact]
    public async Task Sql_is_brought_up_to_date_when_redis_opened_but_sql_missed_it()
    {
        var keys = await ScheduledAsync(startsInMs: -1000, endsInMs: 600_000);
        var loop = Loop();
        await loop.TickAsync(_lease.Current!);   // loads and opens
        (await SqlStatusAsync(keys.AuctionId)).Should().Be(AuctionStatus.Live);

        // As if the SQL update after open had been lost.
        await _db.Database.ExecuteSqlRawAsync("UPDATE catalog.Auctions SET Status = 1 WHERE AuctionId = {0}", keys.AuctionId);
        loop.Reset();

        await loop.TickAsync(_lease.Current!);

        (await SqlStatusAsync(keys.AuctionId)).Should().Be(AuctionStatus.Live);
    }

    [Fact]
    public async Task Stale_grant_stops_the_tick_and_leaves_sql_alone()
    {
        var keys = await ScheduledAsync(startsInMs: -1000, endsInMs: 600_000);
        var stale = _lease.Current! with { Token = _lease.Current!.Token - 1 };

        var tick = () => Loop().TickAsync(stale);

        await tick.Should().ThrowAsync<StaleFencingTokenException>();
        (await SqlStatusAsync(keys.AuctionId)).Should().Be(AuctionStatus.Scheduled);
    }
}
