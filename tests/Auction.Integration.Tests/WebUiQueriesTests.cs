using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Infrastructure.Account;
using Auction.Infrastructure.Admin;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>Spec 06: the new reads behind the purchases, sales and admin pages, on a real SQL Server and Redis.</summary>
[Collection(SqlServerCollection.Name)]
public class WebUiQueriesTests(SqlServerFixture sql, RedisFixture redis)
{
    private static async Task<Guid> AddUserAsync(LifecycleTestDb t, string name, UserStatus status = UserStatus.Active)
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            UserId = Guid.NewGuid(), Username = name, Email = $"{name}@t.local", PasswordHash = "x", Status = status, CreatedAt = now, UpdatedAt = now,
        };
        t.Db.Users.Add(user);
        await t.Db.SaveChangesAsync();
        return user.UserId;
    }

    [Fact]
    public async Task Purchases_and_sales_list_the_settlement_with_the_card()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "ui_settlements");
        var buyer = await AddUserAsync(t, "buyerperson");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Sealed, CurrentPrice: 100m, Leader: buyer, BidCount: 2));
        await new Settler(t.Connections, NullLogger<Settler>.Instance).SettleAsync(t.Grant, a.AuctionId);
        var queries = new SettlementQueries(t.Db);

        var purchases = await queries.ForBuyerAsync(buyer, Paging.From(1, 20));
        var sales = await queries.ForSellerAsync(LifecycleTestDb.Seller, Paging.From(1, 20));
        var nobody = await queries.ForBuyerAsync(Guid.NewGuid(), Paging.From(1, 20));

        purchases.Total.Should().Be(1);
        var row = purchases.Rows.Single();
        row.Card.AuctionId.Should().Be(a.AuctionId);
        row.Card.Title.Should().Be("t");
        row.Card.Status.Should().Be(AuctionStatus.Settled);
        row.HammerPrice.Should().Be(100m);
        row.TotalDue.Should().Be(105m, "5 % premium, free shipping");
        row.Status.Should().Be(SettlementStatus.PendingPayment);
        sales.Rows.Single().BuyerHandle.Should().NotBe("buyerperson", "the seller sees a masked handle");
        nobody.Total.Should().Be(0);
    }

    [Fact]
    public async Task User_search_matches_name_or_email_and_hides_bots()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "ui_users");
        await AddUserAsync(t, "zebra_one");
        await AddUserAsync(t, "other");
        var bot = await AddUserAsync(t, "zebra_bot");
        await t.Db.Users.Where(u => u.UserId == bot).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBot, true));
        var queries = new AdminQueries(t.Db, redis.Connection, TimeProvider.System);

        var (users, total) = await queries.SearchUsersAsync("zebra", Paging.From(1, 20));

        total.Should().Be(1);
        users.Single().Username.Should().Be("zebra_one");
        (await queries.SearchUsersAsync("@t.local", Paging.From(1, 20))).Total.Should().Be(2);
    }

    [Fact]
    public async Task Stats_count_auctions_bids_and_check_health()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "ui_stats");
        var live = await t.AddAuctionAsync(new(AuctionStatus.Live));
        await t.AddAuctionAsync(new(AuctionStatus.Frozen));
        await t.AddAuctionAsync(new(AuctionStatus.Scheduled, StartsInMs: 60_000));
        t.Db.Bids.Add(new Bid
        {
            AuctionId = live.AuctionId, UserId = Guid.NewGuid(), ESeq = 1, Idx = 0, Seq = 1, Kind = BidKind.Manual, Amount = 10m,
            AcceptedAt = DateTime.UtcNow,
        });
        await t.Db.SaveChangesAsync();

        var stats = await new AdminQueries(t.Db, redis.Connection, TimeProvider.System).GetStatsAsync();

        stats.LiveAuctions.Should().Be(1);
        stats.FrozenAuctions.Should().Be(1);
        stats.ScheduledAuctions.Should().Be(1);
        stats.BidsPerMinute.Should().HaveCount(AdminQueries.StatsMinutes);
        stats.BidsLast15Min.Should().Be(1);
        stats.BidsPerMinute[^1].Should().Be(1, "the bid is in the newest minute");
        stats.SqlOk.Should().BeTrue();
        stats.RedisOk.Should().BeTrue();
        stats.LifecycleLeader.Should().Be("test", "LifecycleTestDb holds the lease as test");
    }

    [Fact]
    public async Task Reinstate_makes_a_banned_user_active_and_audits_it()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "ui_reinstate");
        var user = await AddUserAsync(t, "banned_one", UserStatus.Banned);
        var admin = await AddUserAsync(t, "the_admin");
        var manager = new UserAdminManager(t.Db, TestServices.StatusCache(t.Db, redis.Connection), TimeProvider.System);

        await manager.ReinstateAsync(admin, user, "Appeal accepted", "corr-r");

        await using var db = t.NewContext();
        (await db.Users.SingleAsync(u => u.UserId == user)).Status.Should().Be(UserStatus.Active);
        (await db.AuditLog.SingleAsync(x => x.TargetId == user.ToString())).Action.Should().Be("user.reinstate");
    }
}
