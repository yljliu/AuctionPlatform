using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 05 task 5: the real Redis control (control.lua) and the admin actions with AuditLog (R6.1 to R6.5).
/// Uses the normal key layout (partition from RedisOptions.Default), like the Api does.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AuctionControlTests(SqlServerFixture sql, RedisFixture redis)
{
    private static readonly Guid Admin = Guid.Parse("adadadad-0000-0000-0000-000000000005");

    private IDatabase Redis => redis.Connection.GetDatabase();

    private RedisLiveAuctionControl Live() =>
        new(new LifecycleScripts(new ScriptRegistry(redis.Connection)), redis.Connection, RedisOptions.Default);

    private AuctionControlManager Manager(LifecycleTestDb t) => new(t.Db, Live(), new OutboxWriter(t.Db, TimeProvider.System), TimeProvider.System);

    /// <summary>A Live auction in SQL and in Redis.</summary>
    private async Task<(Guid Id, Guid ItemId, AuctionKeys Keys)> LiveAsync(LifecycleTestDb t, long endsInMs = 3_600_000)
    {
        var a = await t.AddAuctionAsync(new(AuctionStatus.Live, EndsInMs: endsInMs));
        var keys = await LiveAuctionSeeder.SeedAsync(Redis, new(a.AuctionId, LifecycleTestDb.Seller, EndsInMs: endsInMs));
        return (a.AuctionId, a.ItemId, keys);
    }

    private Task<BidOutcome> BidAsync(AuctionKeys keys, long maxC)
    {
        var user = Guid.NewGuid();
        return new BidScript(new ScriptRegistry(redis.Connection))
            .RunAsync(keys, user, maxC, BidAmountRules.Fingerprint(user, maxC, "USD"), Guid.NewGuid(), "USD", "t");
    }

    private static async Task<AuctionStatus> SqlStatusAsync(LifecycleTestDb t, Guid id)
    {
        await using var db = t.NewContext();
        return await db.Auctions.Where(a => a.AuctionId == id).Select(a => a.Status).SingleAsync();
    }

    private static async Task<string[]> AuditActionsAsync(LifecycleTestDb t, Guid id)
    {
        await using var db = t.NewContext();
        return await db.AuditLog.Where(x => x.TargetId == id.ToString()).OrderBy(x => x.AuditId).Select(x => x.Action).ToArrayAsync();
    }

    private static async Task FailsWith(Func<Task> act, string code) =>
        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(code);

    // ---------- the race (R6.1) ----------

    [Fact]
    public async Task Seller_cancel_after_a_bid_is_refused_and_the_bid_stands()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "control_race_bid");
        var (id, _, keys) = await LiveAsync(t);
        (await BidAsync(keys, 5000)).Accepted.Should().BeTrue();

        var answer = await Live().CancelAsync(id, force: false);

        answer.Result.Should().Be(LiveControlResult.InvalidState);
        ((string?)await Redis.HashGetAsync(keys.State, "status")).Should().Be("Live");
    }

    [Fact]
    public async Task Bid_after_a_seller_cancel_gets_not_live()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "control_race_cancel");
        var (id, _, keys) = await LiveAsync(t);

        (await Live().CancelAsync(id, force: false)).Result.Should().Be(LiveControlResult.Done);

        (await BidAsync(keys, 5000)).ErrorCode.Should().Be("NOT_LIVE");
    }

    [Fact]
    public async Task Cancel_and_bid_at_once_never_both_win()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "control_race_both");
        for (var i = 0; i < 20; i++)
        {
            var (id, _, keys) = await LiveAsync(t);

            var bid = BidAsync(keys, 5000);
            var cancel = Live().CancelAsync(id, force: false);
            await Task.WhenAll(bid, cancel);
            var bidWon = (await bid).Accepted;
            var cancelWon = (await cancel).Result == LiveControlResult.Done;

            (bidWon && cancelWon).Should().BeFalse("only one can win");
            (bidWon || cancelWon).Should().BeTrue("one of them wins");
        }
    }

    [Fact]
    public async Task Not_in_redis_is_reported()
    {
        (await Live().CancelAsync(Guid.NewGuid(), force: false)).Result.Should().Be(LiveControlResult.NotInRedis);
    }

    // ---------- admin actions (R6.2 to R6.5) ----------

    [Fact]
    public async Task Force_cancel_with_bids_cancels_everywhere_and_is_audited()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "control_force_cancel");
        var (id, itemId, keys) = await LiveAsync(t);
        await BidAsync(keys, 5000);

        await Manager(t).ForceCancelAsync(Admin, id, "Fake listing", "corr-1");

        ((string?)await Redis.HashGetAsync(keys.State, "status")).Should().Be("Cancelled");
        await using var db = t.NewContext();
        var a = await db.Auctions.SingleAsync(x => x.AuctionId == id);
        a.Status.Should().Be(AuctionStatus.Cancelled);
        a.CancelledBy.Should().Be(Admin);
        a.CancelledReason.Should().Be("Fake listing");
        (await db.Items.SingleAsync(i => i.ItemId == itemId)).Status.Should().Be(ItemStatus.Draft);
        (await db.Outbox.CountAsync(o => o.Topic == OutboxTopics.AuctionCancelled && o.Key == id.ToString())).Should().Be(1);
        var audit = await db.AuditLog.SingleAsync(x => x.TargetId == id.ToString());
        audit.Should().Match<Infrastructure.Persistence.Entities.AuditLogEntry>(x =>
            x.Action == "auction.force_cancel" && x.ActorId == Admin && x.Reason == "Fake listing" && x.CorrelationId == "corr-1");
    }

    [Fact]
    public async Task Force_close_ends_now_and_keeps_sql_live_until_seal()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "control_force_close");
        var (id, _, keys) = await LiveAsync(t);

        await Manager(t).ForceCloseAsync(Admin, id, "Seller asked", null);

        ((long)await Redis.HashGetAsync(keys.State, "endMs")).Should().BeCloseTo(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 5000);
        (await SqlStatusAsync(t, id)).Should().Be(AuctionStatus.Live, "the lifecycle loop seals it");
        (await AuditActionsAsync(t, id)).Should().Equal("auction.force_close");
    }

    [Fact]
    public async Task Freeze_makes_bids_fail_with_auction_frozen()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "control_freeze");
        var (id, _, keys) = await LiveAsync(t);

        await Manager(t).FreezeAsync(Admin, id, "Checking a report", null);

        (await BidAsync(keys, 5000)).ErrorCode.Should().Be("AUCTION_FROZEN");
        (await SqlStatusAsync(t, id)).Should().Be(AuctionStatus.Frozen);
        (await AuditActionsAsync(t, id)).Should().Equal("auction.freeze");
    }

    [Fact]
    public async Task Resume_pushes_the_end_later_by_the_frozen_time()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "control_resume");
        var (id, _, keys) = await LiveAsync(t, endsInMs: 600_000);
        var end = (long)await Redis.HashGetAsync(keys.State, "endMs");
        await Manager(t).FreezeAsync(Admin, id, "Checking", null);
        await Task.Delay(500);

        await Manager(t).ResumeAsync(Admin, id, "All fine", null);

        var newEnd = (long)await Redis.HashGetAsync(keys.State, "endMs");
        newEnd.Should().BeGreaterThanOrEqualTo(end + 500);
        (await BidAsync(keys, 5000)).Accepted.Should().BeTrue();
        await using var db = t.NewContext();
        var a = await db.Auctions.SingleAsync(x => x.AuctionId == id);
        a.Status.Should().Be(AuctionStatus.Live);
        new DateTimeOffset(DateTime.SpecifyKind(a.EndTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds().Should().BeCloseTo(newEnd, 5);
        (await AuditActionsAsync(t, id)).Should().Equal("auction.freeze", "auction.resume");
    }

    [Fact]
    public async Task Resume_of_a_live_auction_is_invalid_state()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "control_resume_live");
        var (id, _, _) = await LiveAsync(t);

        await FailsWith(() => Manager(t).ResumeAsync(Admin, id, "x", null), ErrorCodes.InvalidState);
        (await AuditActionsAsync(t, id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Admin_actions_need_a_reason()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "control_reason");
        var (id, _, _) = await LiveAsync(t);

        await FailsWith(() => Manager(t).FreezeAsync(Admin, id, "  ", null), ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Unknown_auction_is_not_found()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "control_missing");

        await FailsWith(() => Manager(t).FreezeAsync(Admin, Guid.NewGuid(), "x", null), ErrorCodes.NotFound);
    }
}
