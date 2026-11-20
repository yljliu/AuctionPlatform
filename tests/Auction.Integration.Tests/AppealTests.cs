using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>Spec 10 task 5: appeals from suspended users, and the admin's decision.</summary>
[Collection(SqlServerCollection.Name)]
public class AppealTests(SqlServerFixture sql, RedisFixture redis)
{
    private const string Password = "correct-horse-battery";
    private static readonly Guid Admin = Guid.Parse("adadadad-0000-0000-0000-0000000000ad");
    private const string Message = "I was in hospital and could not pay. I will pay next time.";

    private AppealManager Appeals(LifecycleTestDb t)
    {
        var db = t.NewContext();
        return new AppealManager(db, TestServices.Auth(db),
            new UserAdminManager(db, new UserStatusCache(redis.Connection, db, NullLogger<UserStatusCache>.Instance), TimeProvider.System),
            TimeProvider.System);
    }

    /// <summary>A user with a real password, suspended by 3 strikes unless told otherwise.</summary>
    private static async Task<(Guid Id, string Email)> UserAsync(LifecycleTestDb t, string name, UserStatus status = UserStatus.Suspended, int strikes = 3)
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            UserId = Guid.NewGuid(), Username = name, Email = $"{name}@t.local", PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, 4),
            Status = status, CreatedAt = now, UpdatedAt = now,
        };
        t.Db.Users.Add(user);
        for (var i = 0; i < strikes; i++)
        {
            t.Db.UserStrikes.Add(new UserStrike { UserId = user.UserId, SettlementId = Guid.NewGuid(), CreatedAt = now });
        }

        await t.Db.SaveChangesAsync();
        return (user.UserId, user.Email);
    }

    private static async Task<DomainException> Fails(Func<Task> call) => (await call.Should().ThrowAsync<DomainException>()).Which;

    [Fact]
    public async Task A_suspended_user_with_the_right_password_opens_one_appeal()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "appeal_open");
        var (id, email) = await UserAsync(t, "bob");

        var status = await Appeals(t).SubmitAsync(email, Password, Message);

        status.Latest!.Status.Should().Be(AppealStatus.Open);
        status.ActiveStrikes.Should().Be(3);
        (await Fails(() => Appeals(t).SubmitAsync(email, Password, Message))).Code.Should().Be(ErrorCodes.AlreadyApplied);
        await using var db = t.NewContext();
        (await db.Appeals.CountAsync(a => a.UserId == id)).Should().Be(1);
    }

    [Fact]
    public async Task Wrong_password_active_user_and_a_short_message_are_refused()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "appeal_refuse");
        var (_, bob) = await UserAsync(t, "bob");
        var (_, alice) = await UserAsync(t, "alice", UserStatus.Active, strikes: 0);

        (await Fails(() => Appeals(t).SubmitAsync(bob, "wrong-password-here", Message))).Code.Should().Be(ErrorCodes.InvalidCredentials);
        (await Fails(() => Appeals(t).SubmitAsync(alice, Password, Message))).Code.Should().Be(ErrorCodes.InvalidState);
        var shortOne = await Fails(() => Appeals(t).SubmitAsync(bob, Password, "please"));
        shortOne.Code.Should().Be(ErrorCodes.ValidationFailed);
        shortOne.Fields.Should().ContainSingle(f => f.Field == "message");
    }

    [Fact]
    public async Task Accept_reinstates_and_clears_the_strikes()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "appeal_accept");
        var (id, email) = await UserAsync(t, "bob");
        var appealId = (await Appeals(t).SubmitAsync(email, Password, Message)).Latest!.AppealId;

        await Appeals(t).ResolveAsync(Admin, appealId, accept: true, note: "OK, one more chance", correlationId: null);

        await using var db = t.NewContext();
        (await db.Users.SingleAsync(u => u.UserId == id)).Status.Should().Be(UserStatus.Active);
        (await db.UserStrikes.CountAsync(s => s.UserId == id && s.ClearedAt == null)).Should().Be(0);
        (await db.UserStrikes.CountAsync(s => s.UserId == id)).Should().Be(3, "kept as history");
        (await db.Appeals.SingleAsync(a => a.AppealId == appealId)).Status.Should().Be(AppealStatus.Accepted);
        (await TestServices.Auth(t.NewContext()).LoginAsync(email, Password, null, null)).User.UserId.Should().Be(id);
        (await Fails(() => Appeals(t).ResolveAsync(Admin, appealId, true, null, null))).Code.Should().Be(ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Reject_needs_a_note_keeps_the_suspension_and_allows_a_new_appeal()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "appeal_reject");
        var (id, email) = await UserAsync(t, "bob");
        var appealId = (await Appeals(t).SubmitAsync(email, Password, Message)).Latest!.AppealId;

        (await Fails(() => Appeals(t).ResolveAsync(Admin, appealId, accept: false, note: " ", correlationId: null))).Code.Should().Be(ErrorCodes.ValidationFailed);
        await Appeals(t).ResolveAsync(Admin, appealId, accept: false, note: "Three unpaid wins in a week.", correlationId: null);

        var status = await Appeals(t).GetStatusAsync(email, Password);
        status.UserStatus.Should().Be(UserStatus.Suspended);
        status.Latest!.Status.Should().Be(AppealStatus.Rejected);
        status.Latest.AdminNote.Should().Be("Three unpaid wins in a week.");
        (await Appeals(t).SubmitAsync(email, Password, Message)).Latest!.Status.Should().Be(AppealStatus.Open);
    }

    [Fact]
    public async Task The_queue_shows_the_reason_and_the_strikes()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "appeal_queue");
        var (id, email) = await UserAsync(t, "bob", strikes: 0);
        var a = await t.AddAuctionAsync(new(AuctionStatus.Settled));
        var s = new Settlement
        {
            SettlementId = Guid.NewGuid(), AuctionId = a.AuctionId, BuyerId = id, SellerId = LifecycleTestDb.Seller, Currency = "USD",
            Status = SettlementStatus.Defaulted, PaymentDeadline = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.Settlements.Add(s);
        t.Db.UserStrikes.Add(new UserStrike { UserId = id, SettlementId = s.SettlementId, CreatedAt = DateTime.UtcNow });
        t.Db.AuditLog.Add(new AuditLogEntry { Action = "user.suspend.strikes", TargetType = "User", TargetId = id.ToString(), Reason = "3 unpaid wins", CreatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();
        await Appeals(t).SubmitAsync(email, Password, Message);

        var row = (await Appeals(t).ListAsync(openOnly: true)).Should().ContainSingle().Subject;

        row.Username.Should().Be("bob");
        row.SuspendReason.Should().Be("3 unpaid wins");
        row.Strikes.Should().ContainSingle(x => x.SettlementId == s.SettlementId && x.Title == "t");
    }
}
