using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Auction.Infrastructure.Redis;

/// <summary>
/// Runs embedded Lua scripts by their SHA1 (EVALSHA), so Redis does not get the full text every call.
/// If Redis forgot the script (restart, failover, SCRIPT FLUSH) it answers NOSCRIPT: the script is
/// loaded again and the call is tried once more (spec 03 design 4).
/// </summary>
public sealed class ScriptRegistry(IConnectionMultiplexer redis)
{
    private sealed record Script(string Source, byte[] Sha);

    private readonly ConcurrentDictionary<string, Script> _scripts = new();

    /// <summary>The SHA1 Redis uses for a script, as 40 lower-case hex letters.</summary>
    public static string ShaHex(string source) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    public async Task<RedisResult> EvaluateAsync(string fileName, RedisKey[] keys, RedisValue[] args)
    {
        var script = _scripts.GetOrAdd(fileName, name =>
        {
            var source = LuaScripts.Load(name);
            return new Script(source, SHA1.HashData(Encoding.UTF8.GetBytes(source)));
        });

        var database = redis.GetDatabase();
        try
        {
            return await database.ScriptEvaluateAsync(script.Sha, keys, args);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOSCRIPT", StringComparison.Ordinal))
        {
            await database.ExecuteAsync("SCRIPT", "LOAD", script.Source);
            return await database.ScriptEvaluateAsync(script.Sha, keys, args);
        }
    }
}
