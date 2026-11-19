using System.Text.Json;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Live;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 04 task 3: snapshots from Redis (Live) and from SQL (not in Redis).
/// The live auction has a reserve of 777.77 and Alice's max is 999.99; neither may show.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class SnapshotBuilderTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
{
    private AuctionDbContext _db = null!;
    private string _connectionString = "";
    private Guid _alice, _bob, _seller, _live, _settled, _draft;

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("snapshots");
        await _db.Database.MigrateAsync();
        _connectionString = sql.ConnectionStringFor("snapshots");
        var now = DateTime.UtcNow;

        User NewUser(string name) => new()
        {
            UserId = Guid.NewGuid(), Username = name, Email = name + "@t.local", PasswordHash = "x",
            Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now,
        };

        var alice = NewUser("alice42x");
        var bob = NewUser("bobsmith42");
        var seller = NewUser("seller1");
        var category = new Category { Name = "c", Slug = "c" };
        _db.Users.AddRange(alice, bob, seller);
        _db.Categories.Add(category);
        _db.BidIncrementTiers.AddRange(
            new BidIncrementTier { TableId = 1, FromPrice = 0, Increment = 1 },
            new BidIncrementTier { TableId = 1, FromPrice = 50, Increment = 2.5m });
        await _db.SaveChangesAsync();
        (_alice, _bob, _seller) = (alice.UserId, bob.UserId, seller.UserId);

        _live = AddAuction(category.CategoryId, AuctionStatus.Live, 777.77m, null, now);
        _settled = AddAuction(category.CategoryId, AuctionStatus.Settled, null, 41m, now);
        _draft = AddAuction(category.CategoryId, AuctionStatus.Draft, null, null, now);
        _db.Bids.AddRange(
            new Bid { AuctionId = _settled, UserId = _alice, ESeq = 1, Idx = 0, Seq = 1, Kind = BidKind.Manual, Amount = 10, MaxAmount = 40, AcceptedAt = now },
            new Bid { AuctionId = _settled, UserId = _bob, ESeq = 2, Idx = 0, Seq = 2, Kind = BidKind.Manual, Amount = 41, MaxAmount = 60, AcceptedAt = now },
            new Bid { AuctionId = _settled, UserId = _bob, ESeq = 3, Idx = 0, Seq = null, Kind = BidKind.MaxRaise, Amount = 41, MaxAmount = 90, AcceptedAt = now });
        _db.Watchlist.Add(new WatchlistEntry { UserId = _alice, AuctionId = _settled, CreatedAt = now });
        await _db.SaveChangesAsync();

        // Live state: price 40.00, Alice leads with a max of 999.99.
        var keys = await LiveAuctionSeeder.SeedAsync(redis.Connection.GetDatabase(),
            new(_live, _seller, PriceC: 4000, Leader: _alice, LeaderMaxC: 99999, BidCount: 2, Inc: "0:100,5000:250"));
        var db = redis.Connection.GetDatabase();
        await db.HashSetAsync(keys.State, [new("reserveC", 77777), new("seq", 2)]);
        await db.SortedSetAddAsync(keys.Bids,
        [
            new(JsonSerializer.Serialize(new PublicBidJson(1, _bob, 2500, 1, "Manual")), 10),
            new(JsonSerializer.Serialize(new PublicBidJson(2, _alice, 4000, 2, "Manual")), 20),
        ]);
    }

    public async Task DisposeAsync()
    {
        var keys = RedisKeys.For(_live, 1);
        await redis.Connection.GetDatabase().KeyDeleteAsync([keys.State, keys.Max, keys.Bids, keys.Events]);
        await _db.DisposeAsync();
    }

    private Guid AddAuction(int categoryId, AuctionStatus status, decimal? reserve, decimal? price, DateTime now)
    {
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = _seller, CategoryId = categoryId, Title = "t", Description = "d",
            ShippingType = ShippingType.FreeShipping, Status = ItemStatus.InAuction, CreatedAt = now, UpdatedAt = now,
        };
        var auction = new AuctionEntity
        {
            AuctionId = Guid.NewGuid(), ItemId = item.ItemId, SellerId = _seller, StartTime = now.AddHours(-2),
            ScheduledEndTime = now.AddHours(1), EndTime = now.AddHours(1), StartingPrice = 10, ReservePrice = reserve, FeeScheduleId = 1,
            Status = status, CurrentPrice = price, CurrentLeaderId = price == null ? null : _bob, BidCount = price == null ? 0 : 2,
            LastSeq = price == null ? 0 : 2, CreatedAt = now, UpdatedAt = now,
        };
        _db.Items.Add(item);
        _db.Auctions.Add(auction);
        return auction.AuctionId;
    }

    private SnapshotBuilder Builder() => new(
        redis.Connection, RedisOptions.Default, _db, HandleCacheFactory.Create(redis.Connection, _connectionString),
        TimeProvider.System, NullLogger<SnapshotBuilder>.Instance);

    [Fact]
    public async Task Live_snapshot_comes_from_redis()
    {
        var snap = await Builder().BuildAsync(_live);

        snap.Seq.Should().Be(2);
        snap.Status.Should().Be("Live");
        snap.CurrentPrice.Units.Should().Be(40);
        snap.MinNextBid.Units.Should().Be(41);
        snap.Leader.Handle.Should().Be("a***2x");
        snap.RecentBids.Select(r => r.Bidder.Handle).Should().Equal("a***2x", "b***42");
        snap.ReserveMet.Should().BeFalse();
    }

    [Fact]
    public async Task Snapshot_has_no_reserve_or_max_values()
    {
        var json = JsonFormatter.Default.Format(await Builder().BuildAsync(_live));

        // Money is written as "units": "777"; times are long numbers, so look for the exact money form.
        json.Should().NotContain("\"units\": \"777\"").And.NotContain("\"units\": \"999\"")
            .And.NotContainEquivalentOf("max").And.NotContain("reservePrice");
    }

    [Fact]
    public async Task Auction_not_in_redis_comes_from_sql_without_max_raise_rows()
    {
        var snap = await Builder().BuildAsync(_settled);

        snap.Status.Should().Be("Settled");
        snap.Seq.Should().Be(2);
        snap.CurrentPrice.Units.Should().Be(41);
        snap.Leader.Handle.Should().Be("b***42");
        snap.RecentBids.Select(r => r.Amount.Units).Should().Equal(41, 10);
        snap.Watchers.Should().Be(1);
        snap.IncrementTiers.Should().HaveCount(2);
        JsonFormatter.Default.Format(snap).Should().NotContain("\"units\": \"90\"").And.NotContain("\"units\": \"60\"");
    }

    [Fact]
    public async Task Draft_or_unknown_auction_is_NOT_FOUND()
    {
        var draft = () => Builder().BuildAsync(_draft);
        var unknown = () => Builder().BuildAsync(Guid.NewGuid());

        (await draft.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.NotFound);
        (await unknown.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.NotFound);
    }
}
