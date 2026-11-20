using Auction.Domain.Enums;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Payments;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>Spec 10 task 3: missed payments, strikes and the suspension at 3 (design W12).</summary>
[Collection(SqlServerCollection.Name)]
public class DefaultStrikeTests(SqlServerFixture sql, RedisFixture redis)
{
    private DefaultSweeper Sweeper(LifecycleTestDb t)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConnectionMultiplexer>(redis.Connection);
        services.AddScoped(_ => t.NewContext());
        services.AddScoped<UserStatusCache>();
        return new DefaultSweeper(t.Connections, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DefaultSweeper>.Instance);
    }

    private static async Task<Guid> UserAsync(LifecycleTestDb t, string name)
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            UserId = Guid.NewGuid(), Username = name, Email = $"{name}@test.local", PasswordHash = "x",
            Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        t.Db.Users.Add(user);
        t.Db.RefreshTokens.Add(new RefreshToken
        {
            TokenId = Guid.NewGuid(), UserId = user.UserId, FamilyId = Guid.NewGuid(), TokenHash = Guid.NewGuid().ToByteArray(),
            ExpiresAt = now.AddDays(1), CreatedAt = now,
        });
        await t.Db.SaveChangesAsync();
        return user.UserId;
    }

    /// <summary>A won auction for the buyer, PendingPayment, deadline already passed. Other bidders get bid rows.</summary>
    private static async Task<(Guid Settlement, Guid Auction)> UnpaidAsync(LifecycleTestDb t, Guid buyer, params (Guid User, decimal Amount)[] others)
    {
        var a = await t.AddAuctionAsync(new(AuctionStatus.Settled, CurrentPrice: 100m, Leader: buyer, BidCount: 1 + others.Length));
        var now = DateTime.UtcNow;
        var eseq = 1;
        foreach (var (user, amount) in others.Append((buyer, 100m)))
        {
            t.Db.Bids.Add(new Bid { AuctionId = a.AuctionId, UserId = user, ESeq = eseq, Idx = 0, Seq = eseq, Kind = BidKind.Manual, Amount = amount, AcceptedAt = now });
            eseq++;
        }

        var s = new Settlement
        {
            SettlementId = Guid.NewGuid(), AuctionId = a.AuctionId, BuyerId = buyer, SellerId = LifecycleTestDb.Seller, Currency = "USD",
            HammerPrice = 100m, TotalDue = 105m, Status = SettlementStatus.PendingPayment,
            PaymentDeadline = now.AddMinutes(-1), CreatedAt = now.AddHours(-73), UpdatedAt = now,
        };
        t.Db.Settlements.Add(s);
        await t.Db.SaveChangesAsync();
        return (s.SettlementId, a.AuctionId);
    }

    [Fact]
    public async Task A_missed_deadline_defaults_and_gives_one_strike()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "default_one");
        var bob = await UserAsync(t, "bob");
        var alice = await UserAsync(t, "alice");
        var (sid, aid) = await UnpaidAsync(t, bob, (alice, 90m));

        var results = await Sweeper(t).RunAsync(t.Grant);

        results.Should().ContainSingle().Which.Should().BeEquivalentTo(new { SettlementId = sid, ActiveStrikes = 1, Suspended = false, NextBidder = (Guid?)alice });
        await using var db = t.NewContext();
        (await db.Settlements.SingleAsync(x => x.SettlementId == sid)).Status.Should().Be(SettlementStatus.Defaulted);
        (await db.Auctions.SingleAsync(x => x.AuctionId == aid)).Outcome.Should().Be(AuctionOutcome.Defaulted);
        (await db.UserStrikes.CountAsync(x => x.UserId == bob)).Should().Be(1);
        (await db.Users.SingleAsync(x => x.UserId == bob)).Status.Should().Be(UserStatus.Active);
        var outbox = await db.Outbox.SingleAsync(x => x.Topic == DefaultSweeper.DefaultedTopic && x.Key == sid.ToString());
        outbox.Payload.Should().Contain("\"activeStrikes\":1").And.Contain("\"canOfferSecondChance\":true");
    }

    [Fact]
    public async Task Running_twice_adds_nothing_more()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "default_twice");
        var bob = await UserAsync(t, "bob");
        await UnpaidAsync(t, bob);

        await Sweeper(t).RunAsync(t.Grant);
        (await Sweeper(t).RunAsync(t.Grant)).Should().BeEmpty();

        await using var db = t.NewContext();
        (await db.UserStrikes.CountAsync(x => x.UserId == bob)).Should().Be(1);
        (await db.Outbox.CountAsync(x => x.Topic == DefaultSweeper.DefaultedTopic)).Should().Be(1);
    }

    [Fact]
    public async Task The_third_strike_suspends_until_an_appeal_and_signs_the_buyer_out()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "default_three");
        var bob = await UserAsync(t, "bob");
        for (var i = 0; i < 3; i++)
        {
            await UnpaidAsync(t, bob);
        }

        var results = await Sweeper(t).RunAsync(t.Grant);

        results.Select(r => r.ActiveStrikes).Should().Equal(1, 2, 3);
        results.Select(r => r.Suspended).Should().Equal(false, false, true);
        await using var db = t.NewContext();
        var user = await db.Users.SingleAsync(x => x.UserId == bob);
        user.Status.Should().Be(UserStatus.Suspended);
        user.SuspendedUntil.Should().BeNull("suspended until an appeal is accepted");
        (await db.RefreshTokens.AnyAsync(x => x.UserId == bob && x.RevokedAt == null)).Should().BeFalse();
        (await db.AuditLog.CountAsync(x => x.Action == "user.suspend.strikes" && x.TargetId == bob.ToString())).Should().Be(1);

        await using var cacheDb = t.NewContext();
        var cache = new UserStatusCache(redis.Connection, cacheDb, NullLogger<UserStatusCache>.Instance);
        (await cache.GetAsync(bob))!.Status.Should().Be(UserStatus.Suspended);
    }

    [Fact]
    public async Task A_fourth_default_while_suspended_adds_a_strike_but_no_second_suspension()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "default_four");
        var bob = await UserAsync(t, "bob");
        for (var i = 0; i < 3; i++)
        {
            await UnpaidAsync(t, bob);
        }

        await Sweeper(t).RunAsync(t.Grant);
        await UnpaidAsync(t, bob);

        var fourth = (await Sweeper(t).RunAsync(t.Grant)).Should().ContainSingle().Subject;

        fourth.ActiveStrikes.Should().Be(4);
        fourth.Suspended.Should().BeFalse("already suspended");
        await using var db = t.NewContext();
        (await db.AuditLog.CountAsync(x => x.Action == "user.suspend.strikes")).Should().Be(1);
    }

    [Fact]
    public async Task Paid_and_not_yet_due_settlements_are_never_touched()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "default_paid");
        var bob = await UserAsync(t, "bob");
        var (sid, _) = await UnpaidAsync(t, bob);
        await t.Db.Settlements.Where(x => x.SettlementId == sid).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SettlementStatus.PaidInFull));
        var (later, _) = await UnpaidAsync(t, bob);
        await t.Db.Settlements.Where(x => x.SettlementId == later).ExecuteUpdateAsync(x => x.SetProperty(s => s.PaymentDeadline, DateTime.UtcNow.AddHours(1)));

        (await Sweeper(t).RunAsync(t.Grant)).Should().BeEmpty();
    }

    [Fact]
    public async Task No_other_active_bidder_means_no_second_chance()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "default_nonext");
        var bob = await UserAsync(t, "bob");
        var banned = await UserAsync(t, "carl");
        await t.Db.Users.Where(x => x.UserId == banned).ExecuteUpdateAsync(x => x.SetProperty(u => u.Status, UserStatus.Banned));
        await UnpaidAsync(t, bob, (banned, 95m));

        (await Sweeper(t).RunAsync(t.Grant)).Single().NextBidder.Should().BeNull();
    }

    [Fact]
    public async Task A_stale_leader_cannot_default()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "default_stale");
        var bob = await UserAsync(t, "bob");
        var (sid, _) = await UnpaidAsync(t, bob);

        var stale = t.Grant with { Token = t.Grant.Token - 1 };
        await FluentActions.Awaiting(() => Sweeper(t).RunAsync(stale)).Should().ThrowAsync<StaleFencingTokenException>();

        await using var db = t.NewContext();
        (await db.Settlements.SingleAsync(x => x.SettlementId == sid)).Status.Should().Be(SettlementStatus.PendingPayment);
    }
}
