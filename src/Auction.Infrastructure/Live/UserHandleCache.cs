using System.Collections.Concurrent;
using Auction.Domain.Identity;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Live;

/// <summary>
/// User id to masked handle for live messages (spec 04 design 2.4). Live streams are shared by many
/// viewers, so handles are always masked here. Order: memory (1 h) → Redis h:{userId} (1 h) → SQL.
/// </summary>
public sealed class UserHandleCache(IConnectionMultiplexer redis, IServiceScopeFactory scopes, TimeProvider clock, ILogger<UserHandleCache> log)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, (string Username, DateTimeOffset Until)> _memory = new();

    /// <summary>Masked handles for these users. Unknown users get "***".</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetMaskedAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var names = await GetUsernamesAsync(userIds, ct);
        return names.ToDictionary(p => p.Key, p => HandleMask.Mask(p.Value));
    }

    /// <summary>Real usernames (for code that masks per viewer itself).</summary>
    public async Task<IReadOnlyDictionary<Guid, string?>> GetUsernamesAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var result = new Dictionary<Guid, string?>();
        var missing = new List<Guid>();

        foreach (var id in userIds.Distinct())
        {
            if (_memory.TryGetValue(id, out var hit) && hit.Until > now)
            {
                result[id] = hit.Username;
            }
            else
            {
                missing.Add(id);
            }
        }

        if (missing.Count == 0)
        {
            return result;
        }

        // Redis next, all in one round trip.
        var stillMissing = new List<Guid>();
        try
        {
            var values = await redis.GetDatabase().StringGetAsync(missing.Select(id => new RedisKey(RedisKeys.Handle(id))).ToArray());
            for (var i = 0; i < missing.Count; i++)
            {
                if (values[i].HasValue)
                {
                    Remember(missing[i], values[i].ToString(), now);
                    result[missing[i]] = values[i].ToString();
                }
                else
                {
                    stillMissing.Add(missing[i]);
                }
            }
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogWarning(ex, "Redis unavailable for user handles; using SQL");
            stillMissing = missing;
        }

        if (stillMissing.Count == 0)
        {
            return result;
        }

        // SQL last, then fill Redis for the next copy that asks.
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();
        var rows = await db.Users.AsNoTracking()
            .Where(u => stillMissing.Contains(u.UserId))
            .Select(u => new { u.UserId, u.Username })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            Remember(row.UserId, row.Username, now);
            result[row.UserId] = row.Username;
            try
            {
                await redis.GetDatabase().StringSetAsync(RedisKeys.Handle(row.UserId), row.Username, Ttl, flags: CommandFlags.FireAndForget);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                log.LogDebug(ex, "Could not cache handle of {UserId}", row.UserId);
            }
        }

        foreach (var id in stillMissing.Where(id => !result.ContainsKey(id)))
        {
            result[id] = null;   // no such user
        }

        return result;
    }

    private void Remember(Guid id, string username, DateTimeOffset now) => _memory[id] = (username, now + Ttl);
}
