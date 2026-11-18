using System.Text.Json;
using Auction.Api.Services;
using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 02 tasks 8 and 9: browse with live Redis numbers, and the auction page.
/// Setup: one seller, two bidders, one Live auction (reserve 777.77) with live state in Redis,
/// one Live auction with no Redis state, and one Cancelled auction.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class LiveAuctionTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
{
    private const decimal Reserve = 777.77m;
    private const long LeaderMaxC = 99_999;   // 999.99, must never show up publicly

    private AuctionDbContext _db = null!;
    private Guid _sellerId, _aliceId, _bobId, _adminId;
    private Guid _liveId, _quietId, _cancelledId;

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("live_auctions");
        await _db.Database.MigrateAsync();
        await SeedSqlAsync();
        await SeedRedisAsync();
    }

    public async Task DisposeAsync()
    {
        var database = redis.Connection.GetDatabase();
        foreach (var id in new[] { _liveId, _quietId, _cancelledId })
        {
            await database.KeyDeleteAsync([RedisKeys.AuctionState(id), RedisKeys.AuctionBids(id), RedisKeys.AuctionMax(id)]);
        }

        await _db.DisposeAsync();
    }

    // ---------- setup ----------

    private async Task SeedSqlAsync()
    {
        var now = DateTime.UtcNow;
        User NewUser(string name) => new()
        {
            UserId = Guid.NewGuid(), Username = name, Email = name + "@test.local", PasswordHash = "x",
            Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now,
        };

        var seller = NewUser("seller1");
        var alice = NewUser("alice42x");
        var bob = NewUser("bobsmith42");
        var admin = NewUser("admin");
        var category = new Category { Name = "Watches", Slug = "watches" };
        _db.Users.AddRange(seller, alice, bob, admin);
        _db.Categories.Add(category);
        _db.BidIncrementTiers.AddRange(
            new BidIncrementTier { TableId = 1, FromPrice = 0, Increment = 1 },
            new BidIncrementTier { TableId = 1, FromPrice = 50, Increment = 2.5m });
        await _db.SaveChangesAsync();
        _db.SellerProfiles.Add(new SellerProfile { UserId = seller.UserId, DisplayName = "Seller Shop", Status = SellerStatus.Verified });

        (_sellerId, _aliceId, _bobId, _adminId) = (seller.UserId, alice.UserId, bob.UserId, admin.UserId);
        _liveId = AddAuction("Omega Seamaster", category.CategoryId, AuctionStatus.Live, Reserve, now.AddMinutes(30), now);
        _quietId = AddAuction("Seiko diver", category.CategoryId, AuctionStatus.Live, null, now.AddMinutes(60), now);
        _cancelledId = AddAuction("Cancelled lamp", category.CategoryId, AuctionStatus.Cancelled, null, now.AddMinutes(90), now);

        _db.Watchlist.Add(new WatchlistEntry { UserId = _aliceId, AuctionId = _quietId, CreatedAt = now });
        await _db.SaveChangesAsync();
    }

    private Guid AddAuction(string title, int categoryId, AuctionStatus status, decimal? reserve, DateTime end, DateTime now)
    {
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = _sellerId, CategoryId = categoryId, Title = title, Description = "d",
            ShippingType = ShippingType.FreeShipping, Status = ItemStatus.InAuction, CreatedAt = now, UpdatedAt = now,
        };
        var auction = new AuctionEntity
        {
            AuctionId = Guid.NewGuid(), ItemId = item.ItemId, SellerId = _sellerId, StartTime = now.AddHours(-1),
            ScheduledEndTime = end, EndTime = end, StartingPrice = 25m, ReservePrice = reserve, FeeScheduleId = 1,
            Status = status, CreatedAt = now, UpdatedAt = now,
        };
        _db.Items.Add(item);
        _db.Auctions.Add(auction);
        return auction.AuctionId;
    }

    /// <summary>The live state spec 03 will keep: price 40.00, 2 bids, Bob leads with a max of 999.99.</summary>
    private async Task SeedRedisAsync()
    {
        var database = redis.Connection.GetDatabase();
        var endMs = DateTimeOffset.UtcNow.AddMinutes(32).ToUnixTimeMilliseconds();   // extended by 2 minutes

        await database.HashSetAsync(RedisKeys.AuctionState(_liveId),
        [
            new("status", "Live"), new("priceC", 4000), new("bidCount", 2), new("endMs", endMs), new("startC", 2500),
            new("reserveC", 77777), new("leader", _bobId.ToString()), new("leaderMaxC", LeaderMaxC), new("seq", 2),
            new("extUsedMs", 120000), new("watchers", 3),
        ]);
        await database.HashSetAsync(RedisKeys.AuctionMax(_liveId),
        [
            new(_bobId.ToString(), LeaderMaxC), new(_aliceId.ToString(), 3500),
        ]);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await database.SortedSetAddAsync(RedisKeys.AuctionBids(_liveId),
        [
            new(JsonSerializer.Serialize(new PublicBidJson(1, _aliceId, 2500, now - 5000, "Manual")), 10),
            new(JsonSerializer.Serialize(new PublicBidJson(2, _bobId, 4000, now - 1000, "Manual")), 20),
        ]);
    }

    private AuctionBrowser Browser(IConnectionMultiplexer connection) =>
        new(new AuctionQueries(_db), new LiveStateReader(connection), _db, NullLogger<AuctionBrowser>.Instance);

    private AuctionDetailReader Details(IConnectionMultiplexer connection) =>
        new(new AuctionManager(_db, new SellerGuard(_db, TestServices.StatusCache(_db, redis.Connection)), new OutboxWriter(_db, TimeProvider.System),
                new StubLiveAuctionControl(), TimeProvider.System),
            new LiveStateReader(connection), _db, NullLogger<AuctionDetailReader>.Instance);

    /// <summary>A Redis connection that can never reach a server, to act as "Redis is down".</summary>
    private static ConnectionMultiplexer DeadRedis() =>
        ConnectionMultiplexer.Connect("127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=500,asyncTimeout=500,connectRetry=0");

    // ---------- task 8: browse overlay ----------

    [Fact]
    public async Task Live_card_shows_the_redis_price_bids_and_end_time()
    {
        var result = await Browser(redis.Connection).BrowseAsync(new AuctionSearch(AuctionStatus.Live), Paging.From(1, 20), viewerId: null);

        var card = result.Rows.Single(r => r.AuctionId == _liveId);
        card.Price.Should().Be(40m);
        card.BidCount.Should().Be(2);
        card.EndTime.Should().BeAfter(DateTime.UtcNow.AddMinutes(31));
        result.PricesDelayed.Should().BeFalse();
    }

    [Fact]
    public async Task Live_card_without_redis_state_keeps_the_sql_values()
    {
        var result = await Browser(redis.Connection).BrowseAsync(new AuctionSearch(AuctionStatus.Live), Paging.From(1, 20), viewerId: null);

        var card = result.Rows.Single(r => r.AuctionId == _quietId);
        card.Price.Should().Be(25m);
        card.BidCount.Should().Be(0);
    }

    [Fact]
    public async Task With_redis_down_browse_still_works_and_flags_delayed()
    {
        using var dead = DeadRedis();

        var result = await Browser(dead).BrowseAsync(new AuctionSearch(AuctionStatus.Live), Paging.From(1, 20), viewerId: null);

        result.Rows.Should().HaveCount(2);
        result.PricesDelayed.Should().BeTrue();
        result.Rows.Single(r => r.AuctionId == _liveId).Price.Should().Be(25m, "SQL still has the starting price");
    }

    [Fact]
    public async Task Watching_is_set_only_for_the_signed_in_viewers_own_list()
    {
        var alice = await Browser(redis.Connection).BrowseAsync(new AuctionSearch(AuctionStatus.Live), Paging.From(1, 20), _aliceId);
        var bob = await Browser(redis.Connection).BrowseAsync(new AuctionSearch(AuctionStatus.Live), Paging.From(1, 20), _bobId);
        var signedOut = await Browser(redis.Connection).BrowseAsync(new AuctionSearch(AuctionStatus.Live), Paging.From(1, 20), null);

        alice.Watching.Should().BeEquivalentTo([_quietId]);
        bob.Watching.Should().BeEmpty();
        signedOut.Watching.Should().BeEmpty();
    }

    // ---------- task 9: auction page ----------

    [Fact]
    public async Task Public_detail_json_never_has_the_reserve_or_any_max()
    {
        var data = await Details(redis.Connection).GetAsync(_liveId, viewerId: null, viewerIsAdmin: false);
        var json = JsonFormatter.Default.Format(AuctionMapper.ToDetail(data));

        data.IsSellerView.Should().BeFalse();
        json.Should().NotContainEquivalentOf("reserve\"");        // no reserve field (reserveMet is fine)
        json.Should().NotContain("reservePrice");
        json.Should().NotContain("\"units\": \"777\"");            // 777.77
        json.Should().NotContain("770000000");
        json.Should().NotContain("\"units\": \"999\"");            // 999.99 leader max
        json.Should().NotContain("myMax");
        json.Should().NotContain("leaderMax");
        json.Should().Contain("\"reserveMet\": false", "only whether it is met is shown");
    }

    [Fact]
    public async Task Detail_uses_live_numbers_and_works_out_min_next()
    {
        var data = await Details(redis.Connection).GetAsync(_liveId, viewerId: null, viewerIsAdmin: false);
        var detail = AuctionMapper.ToDetail(data);

        data.Price.Should().Be(40m);
        data.BidCount.Should().Be(2);
        data.MinNext.Should().Be(41m, "40.00 is below the 50.00 tier, so the increment is 1.00");
        data.ExtensionUsedSec.Should().Be(120);
        data.Watchers.Should().Be(3);
        detail.RecentBids.Select(b => b.Seq).Should().Equal(2, 1);   // newest first
        detail.IncrementTiers.Should().HaveCount(2);
    }

    [Fact]
    public async Task Bidder_handles_are_masked_for_the_public()
    {
        var data = await Details(redis.Connection).GetAsync(_liveId, viewerId: null, viewerIsAdmin: false);

        data.RecentBids.Select(b => b.Handle).Should().Equal("b***42", "a***2x");
        data.LeaderHandle.Should().Be("b***42");
    }

    [Fact]
    public async Task Bidder_sees_their_own_name_and_my_status_but_not_others_max()
    {
        var data = await Details(redis.Connection).GetAsync(_liveId, _aliceId, viewerIsAdmin: false);
        var json = JsonFormatter.Default.Format(AuctionMapper.ToDetail(data));

        data.RecentBids.Single(b => b.BidderId == _aliceId).Handle.Should().Be("alice42x");
        data.RecentBids.Single(b => b.BidderId == _bobId).Handle.Should().Be("b***42");
        data.MyStatus!.MyMax.Should().Be(35m);
        data.MyStatus.IsLeader.Should().BeFalse();
        json.Should().NotContain("\"units\": \"999\"", "Bob's max is not Alice's business");
    }

    [Fact]
    public async Task Leader_sees_they_lead()
    {
        var data = await Details(redis.Connection).GetAsync(_liveId, _bobId, viewerIsAdmin: false);

        data.MyStatus!.IsLeader.Should().BeTrue();
        data.MyStatus.MyMax.Should().Be(999.99m, "your own max is yours to see");
    }

    [Fact]
    public async Task Viewer_who_never_bid_has_no_my_status()
    {
        var data = await Details(redis.Connection).GetAsync(_quietId, _aliceId, viewerIsAdmin: false);

        data.MyStatus.Should().BeNull();
    }

    [Fact]
    public async Task Owner_gets_the_seller_view_with_the_reserve()
    {
        var data = await Details(redis.Connection).GetAsync(_liveId, _sellerId, viewerIsAdmin: false);
        var sellerView = AuctionMapper.ToSellerAuction(data);

        data.IsSellerView.Should().BeTrue();
        sellerView.ReservePrice.Units.Should().Be(777);
        sellerView.RowVersion.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Admin_gets_the_seller_view_and_real_names()
    {
        var data = await Details(redis.Connection).GetAsync(_liveId, _adminId, viewerIsAdmin: true);

        data.IsSellerView.Should().BeTrue();
        data.RecentBids.Select(b => b.Handle).Should().Equal("bobsmith42", "alice42x");
    }

    [Fact]
    public async Task Cancelled_auction_is_not_found_for_the_public_but_visible_to_the_owner()
    {
        var act = () => Details(redis.Connection).GetAsync(_cancelledId, _aliceId, viewerIsAdmin: false);

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.NotFound);
        (await Details(redis.Connection).GetAsync(_cancelledId, _sellerId, viewerIsAdmin: false)).Status.Should().Be(AuctionStatus.Cancelled);
    }

    [Fact]
    public async Task Unknown_auction_is_NOT_FOUND()
    {
        var act = () => Details(redis.Connection).GetAsync(Guid.NewGuid(), null, viewerIsAdmin: false);

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task With_redis_down_the_page_still_loads_from_sql()
    {
        using var dead = DeadRedis();

        var data = await Details(dead).GetAsync(_liveId, viewerId: null, viewerIsAdmin: false);

        data.PricesDelayed.Should().BeTrue();
        data.Price.Should().Be(25m);
        data.RecentBids.Should().BeEmpty();
        AuctionMapper.ToDetail(data).Card.PricesDelayed.Should().BeTrue();
    }
}
