using System.Text.Json;
using System.Text.Json.Serialization;
using Auction.Domain.Enums;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Identity;

public sealed record UserStatusInfo(UserStatus Status, int StatusVersion);

/// <summary>
/// Fast user status check for bids (design 5.4). Reads Redis key {u:ID}:status,
/// falls back to SQL and caches for 10 minutes. If Redis is down, SQL still answers.
/// </summary>
public sealed class UserStatusCache(IConnectionMultiplexer redis, AuctionDbContext db, ILogger<UserStatusCache> log)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public async Task<UserStatusInfo?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var key = RedisKeys.UserStatus(userId);

        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            if (value.HasValue)
            {
                var cached = JsonSerializer.Deserialize<CachedStatus>(value.ToString());
                if (cached != null)
                {
                    return new UserStatusInfo((UserStatus)cached.Status, cached.Sv);
                }
            }
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException or JsonException)
        {
            log.LogWarning(ex, "Could not read user status from Redis for {UserId}; using SQL", userId);
        }

        var row = await db.Users.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => new UserStatusInfo(u.Status, u.StatusVersion))
            .FirstOrDefaultAsync(ct);

        if (row != null)
        {
            await SetAsync(userId, row);
        }

        return row;
    }

    /// <summary>Overwrites the cached status. Called after an admin ban or suspend.</summary>
    public async Task SetAsync(Guid userId, UserStatusInfo info)
    {
        try
        {
            var json = JsonSerializer.Serialize(new CachedStatus((byte)info.Status, info.StatusVersion));
            await redis.GetDatabase().StringSetAsync(RedisKeys.UserStatus(userId), json, Ttl);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogWarning(ex, "Could not write user status to Redis for {UserId}", userId);
        }
    }

    private sealed record CachedStatus(
        [property: JsonPropertyName("status")] byte Status,
        [property: JsonPropertyName("sv")] int Sv);
}
