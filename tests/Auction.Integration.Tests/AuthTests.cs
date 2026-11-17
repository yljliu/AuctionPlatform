using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Persistence;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Auction.Integration.Tests;

[Collection(SqlServerCollection.Name)]
public class AuthTests(SqlServerFixture sql)
{
    private const string Password = "a-good-long-password";

    private async Task<AuctionDbContext> NewDbAsync(string name)
    {
        var db = await sql.CreateEmptyContextAsync("auth_" + name);
        await db.Database.MigrateAsync();
        return db;
    }

    private static async Task<DomainException> FailsWith(Func<Task> act, string code)
    {
        var ex = (await act.Should().ThrowAsync<DomainException>()).Which;
        ex.Code.Should().Be(code);
        return ex;
    }

    // ---------- register (R3) ----------

    [Fact]
    public async Task Register_creates_an_active_bidder_with_a_bcrypt_hash()
    {
        await using var db = await NewDbAsync("register_ok");
        var auth = TestServices.Auth(db);

        var id = await auth.RegisterAsync("alice", "alice@example.com", Password);

        var user = await db.Users.Include(u => u.Roles).SingleAsync();
        user.UserId.Should().Be(id);
        user.Status.Should().Be(UserStatus.Active);
        user.Roles.Select(r => r.Role).Should().Equal(Role.Bidder);
        user.PasswordHash.Should().StartWith("$2").And.NotContain(Password);
        user.PasswordHash.Should().Contain("$12$");   // cost 12
    }

    [Fact]
    public async Task Register_with_a_taken_username_returns_USERNAME_TAKEN()
    {
        await using var db = await NewDbAsync("register_name_taken");
        var auth = TestServices.Auth(db);
        await auth.RegisterAsync("alice", "alice@example.com", Password);

        await FailsWith(() => auth.RegisterAsync("ALICE", "other@example.com", Password), ErrorCodes.UsernameTaken);
    }

    [Fact]
    public async Task Register_with_a_taken_email_looks_like_success_but_creates_nothing()
    {
        await using var db = await NewDbAsync("register_email_taken");
        var auth = TestServices.Auth(db);
        var firstId = await auth.RegisterAsync("alice", "alice@example.com", Password);

        var secondId = await auth.RegisterAsync("alice2", "ALICE@example.com", Password);

        secondId.Should().NotBe(Guid.Empty).And.NotBe(firstId);
        (await db.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Register_with_bad_fields_returns_one_issue_per_field()
    {
        await using var db = await NewDbAsync("register_bad_fields");

        var ex = await FailsWith(() => TestServices.Auth(db).RegisterAsync("x", "nope", "short"), ErrorCodes.ValidationFailed);

        ex.Fields.Select(f => f.Field).Should().Equal("username", "email", "password");
    }

    // ---------- login (R4.1 to R4.4) ----------

    [Fact]
    public async Task Login_returns_tokens_and_me()
    {
        await using var db = await NewDbAsync("login_ok");
        var auth = TestServices.Auth(db);
        await auth.RegisterAsync("alice", "alice@example.com", Password);

        var result = await auth.LoginAsync("Alice@Example.com", Password, "test", "127.0.0.1");

        result.User.Username.Should().Be("alice");
        result.Tokens.AccessToken.Should().NotBeNullOrEmpty();
        result.Tokens.AccessExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(10), TimeSpan.FromSeconds(30));
        result.Tokens.RefreshExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(14), TimeSpan.FromMinutes(1));
        (await db.RefreshTokens.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Wrong_password_returns_INVALID_CREDENTIALS_and_counts_the_failure()
    {
        await using var db = await NewDbAsync("login_wrong");
        var auth = TestServices.Auth(db);
        await auth.RegisterAsync("alice", "alice@example.com", Password);

        await FailsWith(() => auth.LoginAsync("alice@example.com", "wrong-password-123", null, null), ErrorCodes.InvalidCredentials);

        (await db.Users.AsNoTracking().SingleAsync()).FailedLogins.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_email_returns_INVALID_CREDENTIALS()
    {
        await using var db = await NewDbAsync("login_unknown");

        await FailsWith(() => TestServices.Auth(db).LoginAsync("nobody@example.com", Password, null, null), ErrorCodes.InvalidCredentials);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        await using var db = await NewDbAsync("login_lockout");
        var auth = TestServices.Auth(db);
        await auth.RegisterAsync("alice", "alice@example.com", Password);

        for (var i = 0; i < 5; i++)
        {
            await FailsWith(() => auth.LoginAsync("alice@example.com", "wrong-password-123", null, null), ErrorCodes.InvalidCredentials);
        }

        // Even the right password is refused while locked.
        var ex = await FailsWith(() => auth.LoginAsync("alice@example.com", Password, null, null), ErrorCodes.AccountLocked);
        ex.RetryAfterMs.Should().BeGreaterThan(0);
        (await db.Users.AsNoTracking().SingleAsync()).LockedUntil.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task Banned_user_gets_ACCOUNT_BANNED()
    {
        await using var db = await NewDbAsync("login_banned");
        var auth = TestServices.Auth(db);
        await auth.RegisterAsync("alice", "alice@example.com", Password);
        await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Banned));
        db.ChangeTracker.Clear();   // forget the copy loaded by Register, like a new request would

        await FailsWith(() => auth.LoginAsync("alice@example.com", Password, null, null), ErrorCodes.AccountBanned);
    }

    // ---------- refresh and logout (R4.5 to R4.7) ----------

    [Fact]
    public async Task Refresh_rotates_the_token_in_the_same_family()
    {
        await using var db = await NewDbAsync("refresh_ok");
        var auth = TestServices.Auth(db);
        await auth.RegisterAsync("alice", "alice@example.com", Password);
        var login = await auth.LoginAsync("alice@example.com", Password, null, null);

        var refreshed = await auth.RefreshAsync(login.Tokens.RefreshToken, null, null);

        refreshed.Tokens.RefreshToken.Should().NotBe(login.Tokens.RefreshToken);
        var rows = await db.RefreshTokens.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.FamilyId).Distinct().Should().ContainSingle();
        rows[0].RevokedAt.Should().NotBeNull();
        rows[0].ReplacedBy.Should().Be(rows[1].TokenId);
        rows[1].RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Reusing_an_old_refresh_token_revokes_the_whole_family()
    {
        await using var db = await NewDbAsync("refresh_reuse");
        var auth = TestServices.Auth(db);
        await auth.RegisterAsync("alice", "alice@example.com", Password);
        var login = await auth.LoginAsync("alice@example.com", Password, null, null);
        var refreshed = await auth.RefreshAsync(login.Tokens.RefreshToken, null, null);

        await FailsWith(() => auth.RefreshAsync(login.Tokens.RefreshToken, null, null), ErrorCodes.RefreshReused);

        // The new token was revoked too.
        await FailsWith(() => auth.RefreshAsync(refreshed.Tokens.RefreshToken, null, null), ErrorCodes.RefreshReused);
    }

    [Fact]
    public async Task Unknown_refresh_token_returns_UNAUTHENTICATED()
    {
        await using var db = await NewDbAsync("refresh_unknown");

        await FailsWith(() => TestServices.Auth(db).RefreshAsync("not-a-token", null, null), ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        await using var db = await NewDbAsync("logout");
        var auth = TestServices.Auth(db);
        await auth.RegisterAsync("alice", "alice@example.com", Password);
        var login = await auth.LoginAsync("alice@example.com", Password, null, null);

        await auth.LogoutAsync(login.Tokens.RefreshToken);

        (await db.RefreshTokens.AsNoTracking().SingleAsync()).RevokedAt.Should().NotBeNull();
    }
}
