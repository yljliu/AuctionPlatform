using System.Text.Json;
using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 03 task 10: GetBidHistory, GetMyBidStatus, ListMyBids.
/// One Live auction (Redis + SQL bids) and one Settled auction (SQL only) won by Bob.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class BidReadTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
{
    private AuctionDbContext _db = null!;
    private Guid _alice, _bob, _seller, _live, _settled;

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("bid_reads");
        await _db.Database.MigrateAsync();
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
        _db.BidIncrementTiers.Add(new BidIncrementTier { TableId = 1, FromPrice = 0, Increment = 1 });
        await _db.SaveChangesAsync();
        (_alice, _bob, _seller) = (alice.UserId, bob.UserId, seller.UserId);

        _live = AddAuction("Live watch", category.CategoryId, AuctionStatus.Live, null, price: 31m, leader: _alice, bidCount: 2, now);
        _settled = AddAuction("Old coin", category.CategoryId, AuctionStatus.Settled, AuctionOutcome.Sold, price: 60m, leader: _bob, bidCount: 2, now);

        // SQL bids (as the persister wrote them).
        AddBid(_live, _alice, 1, 0, 1, BidKind.Manual, 10m, 50m, now);
        AddBid(_live, _bob, 2, 0, 2, BidKind.AutoOutbid, 30m, 30m, now);
        AddBid(_live, _alice, 2, 1, 2, BidKind.ProxyAuto, 31m, null, now);
        AddBid(_settled, _alice, 1, 0, 1, BidKind.Manual, 10m, 40m, now);
        AddBid(_settled, _bob, 2, 0, 2, BidKind.Manual, 41m, 60m, now);
        await _db.SaveChangesAsync();

        // Live state in Redis.
        var keys = await LiveAuctionSeeder.SeedAsync(redis.Connection.GetDatabase(),
            new(_live, _seller, PriceC: 3100, Leader: _alice, LeaderMaxC: 5000, BidCount: 2, Inc: "0:100"));
        var db = redis.Connection.GetDatabase();
        await db.HashSetAsync(keys.Max, _bob.ToString(), 3000);
        await db.SortedSetAddAsync(keys.Bids,
        [
            new(JsonSerializer.Serialize(new PublicBidJson(1, _alice, 1000, 1, "Manual")), 10),
            new(JsonSerializer.Serialize(new PublicBidJson(2, _bob, 3000, 2, "AutoOutbid")), 21),
            new(JsonSerializer.Serialize(new PublicBidJson(2, _alice, 3100, 2, "ProxyAuto")), 22),
        ]);
    }

    public async Task DisposeAsync()
    {
        var keys = RedisKeys.For(_live, 1);
        await redis.Connection.GetDatabase().KeyDeleteAsync([keys.State, keys.Max, keys.Bids, keys.Events]);
        await _db.DisposeAsync();
    }

    private Guid AddAuction(string title, int categoryId, AuctionStatus status, AuctionOutcome? outcome, decimal price, Guid leader, int bidCount, DateTime now)
    {
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = _seller, CategoryId = categoryId, Title = title, Description = "d",
            ShippingType = ShippingType.FreeShipping, Status = ItemStatus.InAuction, CreatedAt = now, UpdatedAt = now,
        };
        var auction = new AuctionEntity
        {
            AuctionId = Guid.NewGuid(), ItemId = item.ItemId, SellerId = _seller, StartTime = now.AddHours(-2),
            ScheduledEndTime = now.AddHours(1), EndTime = now.AddHours(1), StartingPrice = 10, FeeScheduleId = 1, Status = status,
            Outcome = outcome, CurrentPrice = price, CurrentLeaderId = leader, BidCount = bidCount, CreatedAt = now, UpdatedAt = now,
        };
        _db.Items.Add(item);
        _db.Auctions.Add(auction);
        return auction.AuctionId;
    }

    private void AddBid(Guid auction, Guid user, long eseq, byte idx, long seq, BidKind kind, decimal amount, decimal? max, DateTime now) =>
        _db.Bids.Add(new Bid
        {
            AuctionId = auction, UserId = user, ESeq = eseq, Idx = idx, Seq = seq, Kind = kind, Amount = amount, MaxAmount = max,
            AcceptedAt = now, IdempotencyKey = max == null ? null : Guid.NewGuid(),
        });

    private BidReadService Service() => new(
        _db,
        new LiveStateReader(redis.Connection),
        new Infrastructure.Bidding.IncrementTableRepository(_db),
        redis.Connection,
        RedisOptions.Default,
        NullLogger<BidReadService>.Instance);

    // ---------- GetBidHistory ----------

    [Fact]
    public async Task Others_see_masked_handles_newest_first_from_redis()
    {
        var history = await Service().GetHistoryAsync(_live, viewerId: null, viewerIsAdmin: false, includeVoided: false, Paging.From(1, 20));

        history.Rows.Select(r => r.Handle).Should().Equal("a***2x", "b***42", "a***2x");
        history.Rows.Select(r => r.Seq).Should().Equal(2, 2, 1);
        history.BidCount.Should().Be(2);
    }

    [Fact]
    public async Task The_bidder_sees_their_own_name_only()
    {
        var history = await Service().GetHistoryAsync(_live, _bob, viewerIsAdmin: false, includeVoided: false, Paging.From(1, 20));

        history.Rows.Where(r => r.BidderId == _bob).Should().OnlyContain(r => r.Handle == "bobsmith42");
        history.Rows.Where(r => r.BidderId == _alice).Should().OnlyContain(r => r.Handle == "a***2x");
    }

    [Fact]
    public async Task History_never_carries_a_max()
    {
        var live = await Service().GetHistoryAsync(_live, _alice, viewerIsAdmin: false, includeVoided: false, Paging.From(1, 20));
        var settled = await Service().GetHistoryAsync(_settled, _alice, viewerIsAdmin: false, includeVoided: false, Paging.From(1, 20));

        // Maxes in this data: 50, 30 (equal to an amount), 40, 60 (equal to an amount). 50 and 40 must never appear.
        live.Rows.Concat(settled.Rows).Select(r => r.Amount).Should().NotContain([50m, 40m]);
        typeof(BidHistoryRow).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Max"));
    }

    [Fact]
    public async Task Settled_history_comes_from_sql()
    {
        var history = await Service().GetHistoryAsync(_settled, null, viewerIsAdmin: false, includeVoided: false, Paging.From(1, 20));

        history.Rows.Select(r => r.Amount).Should().Equal(41m, 10m);
        history.Rows.Select(r => r.Handle).Should().Equal("b***42", "a***2x");
    }

    [Fact]
    public async Task Page_2_of_a_live_auction_comes_from_sql()
    {
        var history = await Service().GetHistoryAsync(_live, null, viewerIsAdmin: false, includeVoided: false, Paging.From(2, 2));

        history.Rows.Should().ContainSingle().Which.Amount.Should().Be(10m);
    }

    [Fact]
    public async Task Voided_bids_are_for_admins_only()
    {
        var act = () => Service().GetHistoryAsync(_live, _alice, viewerIsAdmin: false, includeVoided: true, Paging.From(1, 20));

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.RoleRequired);
    }

    // ---------- GetMyBidStatus ----------

    [Fact]
    public async Task My_status_on_a_live_auction()
    {
        var alice = await Service().GetMyStatusAsync(_live, _alice);
        var bob = await Service().GetMyStatusAsync(_live, _bob);

        alice.IsLeader.Should().BeTrue();
        alice.MyMax.Should().Be(50m);
        bob.IsLeader.Should().BeFalse();
        bob.MyMax.Should().Be(30m);
        bob.MinNext.Should().Be(32m, "31.00 + 1.00");
    }

    [Fact]
    public async Task My_status_on_a_settled_auction_comes_from_sql()
    {
        var bob = await Service().GetMyStatusAsync(_settled, _bob);

        bob.IsLeader.Should().BeTrue();
        bob.MyMax.Should().Be(60m);
    }

    [Fact]
    public async Task My_status_on_an_unknown_auction_is_NOT_FOUND()
    {
        var act = () => Service().GetMyStatusAsync(Guid.NewGuid(), _bob);

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.NotFound);
    }

    // ---------- ListMyBids ----------

    [Fact]
    public async Task Active_lists_only_open_auctions_with_my_numbers()
    {
        var (rows, total) = await Service().ListMineAsync(_bob, MyBidsFilter.Active, Paging.From(1, 20));

        total.Should().Be(1);
        var row = rows.Single();
        row.Card.AuctionId.Should().Be(_live);
        row.MyMax.Should().Be(30m);
        row.MyLastAmount.Should().Be(30m);
        row.IsLeader.Should().BeFalse();
        row.Outcome.Should().BeEmpty();
    }

    [Fact]
    public async Task Won_and_lost_follow_the_settled_outcome()
    {
        var bobWon = await Service().ListMineAsync(_bob, MyBidsFilter.Won, Paging.From(1, 20));
        var aliceLost = await Service().ListMineAsync(_alice, MyBidsFilter.Lost, Paging.From(1, 20));
        var aliceWon = await Service().ListMineAsync(_alice, MyBidsFilter.Won, Paging.From(1, 20));

        bobWon.Rows.Single().Outcome.Should().Be("Won");
        aliceLost.Rows.Single().Card.AuctionId.Should().Be(_settled);
        aliceLost.Rows.Single().Outcome.Should().Be("Lost");
        aliceWon.Total.Should().Be(0);
    }

    [Fact]
    public async Task All_lists_everything_I_bid_on()
    {
        var (rows, total) = await Service().ListMineAsync(_alice, MyBidsFilter.All, Paging.From(1, 20));

        total.Should().Be(2);
        rows.Single(r => r.Card.AuctionId == _live).IsLeader.Should().BeTrue("Redis says Alice leads");
        rows.Single(r => r.Card.AuctionId == _live).MyLastAmount.Should().Be(31m, "her proxy row is her latest amount");
    }
}
