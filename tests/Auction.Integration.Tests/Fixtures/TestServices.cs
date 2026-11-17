using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Auction.Integration.Tests.Fixtures;

/// <summary>Builds the real services by hand for tests.</summary>
public static class TestServices
{
    public static readonly JwtOptions Jwt = new() { SigningKey = "test-signing-key-that-is-at-least-32-bytes!" };

    public static TokenService Tokens() => new(Jwt, TimeProvider.System);

    public static AuthManager Auth(AuctionDbContext db) => new(db, Tokens(), TimeProvider.System);

    public static UserStatusCache StatusCache(AuctionDbContext db, IConnectionMultiplexer redis) =>
        new(redis, db, NullLogger<UserStatusCache>.Instance);

    public static UserAdminManager Admin(AuctionDbContext db, IConnectionMultiplexer redis) =>
        new(db, StatusCache(db, redis), TimeProvider.System);
}
