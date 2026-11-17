using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auction.Integration.Tests;

[Collection(SqlServerCollection.Name)]
public class AdminAndRateLimitTests(SqlServerFixture sql, RedisFixture redis)
{
    private const string Password = "a-good-long-password";

    // ---------- ban and status cache (R5.3) ----------

    [Fact]
    public async Task Ban_is_seen_by_the_status_cache_within_one_second()
    {
        await using var db = await sql.CreateEmptyContextAsync("admin_ban");
        await db.Database.MigrateAsync();
        var auth = TestServices.Auth(db);
        var adminId = Guid.NewGuid();
        var userId = await auth.RegisterAsync("alice", "alice@example.com", Password);
        await auth.LoginAsync("alice@example.com", Password, null, null);

        var cache = TestServices.StatusCache(db, redis.Connection);
        (await cache.GetAsync(userId))!.Status.Should().Be(UserStatus.Active);   // warm the cache

        await TestServices.Admin(db, redis.Connection).BanAsync(adminId, userId, "spam", "corr-1");

        var watch = Stopwatch.StartNew();
        var status = await cache.GetAsync(userId);
        watch.Stop();

        status!.Status.Should().Be(UserStatus.Banned);
        status.StatusVersion.Should().Be(1);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Ban_revokes_tokens_and_writes_the_audit_log()
    {
        await using var db = await sql.CreateEmptyContextAsync("admin_ban_audit");
        await db.Database.MigrateAsync();
        var auth = TestServices.Auth(db);
        var userId = await auth.RegisterAsync("alice", "alice@example.com", Password);
        var login = await auth.LoginAsync("alice@example.com", Password, null, null);

        await TestServices.Admin(db, redis.Connection).BanAsync(Guid.NewGuid(), userId, "spam", "corr-2");

        (await db.RefreshTokens.AsNoTracking().SingleAsync()).RevokedAt.Should().NotBeNull();
        var audit = await db.AuditLog.SingleAsync();
        audit.Action.Should().Be("user.ban");
        audit.CorrelationId.Should().Be("corr-2");

        var refresh = async () => await auth.RefreshAsync(login.Tokens.RefreshToken, null, null);
        (await refresh.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.RefreshReused);
    }

    [Fact]
    public async Task Ban_needs_a_reason()
    {
        await using var db = await sql.CreateEmptyContextAsync("admin_ban_reason");
        await db.Database.MigrateAsync();

        var act = () => TestServices.Admin(db, redis.Connection).BanAsync(Guid.NewGuid(), Guid.NewGuid(), " ", null);

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ---------- rate limiter (R7.1) ----------

    [Fact]
    public async Task Eleventh_login_in_a_minute_is_rate_limited()
    {
        var limiter = new RedisRateLimiter(redis.Connection, NullLogger<RedisRateLimiter>.Instance);
        var ip = "10.0.0." + Random.Shared.Next(1, 250) + "-" + Guid.NewGuid().ToString("N")[..6];

        for (var i = 0; i < 10; i++)
        {
            (await limiter.TryAcquireAsync(RateLimitPolicies.Login, ip)).Allowed.Should().BeTrue($"call {i + 1} is inside the limit");
        }

        var eleventh = await limiter.TryAcquireAsync(RateLimitPolicies.Login, ip);

        eleventh.Allowed.Should().BeFalse();
        eleventh.RetryAfterMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Different_ips_have_their_own_bucket()
    {
        var limiter = new RedisRateLimiter(redis.Connection, NullLogger<RedisRateLimiter>.Instance);
        var first = "ip-a-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 10; i++)
        {
            await limiter.TryAcquireAsync(RateLimitPolicies.Login, first);
        }

        (await limiter.TryAcquireAsync(RateLimitPolicies.Login, "ip-b-" + Guid.NewGuid().ToString("N"))).Allowed.Should().BeTrue();
    }

    // ---------- access token ----------

    [Fact]
    public void Access_token_has_the_expected_claims()
    {
        var user = new Infrastructure.Persistence.Entities.User { UserId = Guid.NewGuid(), Username = "alice", StatusVersion = 3 };

        var (token, expires) = TestServices.Tokens().CreateAccessToken(user, [Role.Bidder, Role.Seller]);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Single(c => c.Type == AuthClaims.Subject).Value.Should().Be(user.UserId.ToString());
        jwt.Claims.Single(c => c.Type == AuthClaims.Name).Value.Should().Be("alice");
        jwt.Claims.Single(c => c.Type == AuthClaims.StatusVersion).Value.Should().Be("3");
        jwt.Claims.Where(c => c.Type == AuthClaims.Role).Select(c => c.Value).Should().BeEquivalentTo("Bidder", "Seller");
        jwt.Header.Alg.Should().Be("HS256");
        expires.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(10), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Short_signing_key_is_refused()
    {
        var act = () => new TokenService(new JwtOptions { SigningKey = "too-short" }, TimeProvider.System);

        act.Should().Throw<InvalidOperationException>();
    }
}
