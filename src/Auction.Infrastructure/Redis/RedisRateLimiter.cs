using System.Reflection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Redis;

public sealed record RateLimitPolicy(string Name, int Capacity, double RefillPerSecond);

public sealed record RateLimitResult(bool Allowed, long RetryAfterMs);

public static class RateLimitPolicies
{
    /// <summary>Login: 10 per minute per IP (R7.1).</summary>
    public static readonly RateLimitPolicy Login = new("login", 10, 10 / 60.0);

    /// <summary>PlaceBid: 5 per second per user (R7.2, used by spec 03).</summary>
    public static readonly RateLimitPolicy Bid = new("bid", 5, 5);
}

public interface IRateLimiter
{
    Task<RateLimitResult> TryAcquireAsync(RateLimitPolicy policy, string id, int cost = 1);
}

/// <summary>Token bucket in Redis. If Redis is down the call is allowed (fail open) and a warning is logged.</summary>
public sealed class RedisRateLimiter(IConnectionMultiplexer redis, ILogger<RedisRateLimiter> log) : IRateLimiter
{
    private static readonly string Script = LuaScripts.Load("token_bucket.lua");

    public async Task<RateLimitResult> TryAcquireAsync(RateLimitPolicy policy, string id, int cost = 1)
    {
        try
        {
            var result = (RedisResult[]?)await redis.GetDatabase().ScriptEvaluateAsync(
                Script,
                [new RedisKey(RedisKeys.RateLimit(policy.Name, id))],
                [policy.Capacity, policy.RefillPerSecond, cost]);

            if (result == null || result.Length < 2)
            {
                return new RateLimitResult(true, 0);
            }

            return new RateLimitResult((long)result[0] == 1, (long)result[1]);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogWarning(ex, "Rate limiter could not reach Redis; allowing {Policy} for {Id}", policy.Name, id);
            return new RateLimitResult(true, 0);
        }
    }
}

/// <summary>Reads Lua scripts embedded from Redis/Lua/*.lua.</summary>
public static class LuaScripts
{
    public static string Load(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
