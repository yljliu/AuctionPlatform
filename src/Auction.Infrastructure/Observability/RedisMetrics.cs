using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Observability;

/// <summary>redis_replica_connected (1 or 0) and redis_memory_used_bytes, filled by <see cref="RedisInfoMonitor"/> (meter "Auction.Redis").</summary>
public static class RedisMetrics
{
    public const string MeterName = "Auction.Redis";

    private static readonly Meter Meter = new(MeterName);
    private static int _replicas = -1;
    private static long _memory;

    static RedisMetrics()
    {
        Meter.CreateObservableGauge("redis_replica_connected", () => Volatile.Read(ref _replicas) > 0 ? 1 : 0,
            description: "1 when the Redis primary reports at least one connected replica");
        Meter.CreateObservableGauge("redis_memory_used_bytes", () => Interlocked.Read(ref _memory),
            description: "used_memory of the Redis primary");
    }

    /// <summary>Replicas the primary reported last time, or -1 before the first reading.</summary>
    public static int Replicas => Volatile.Read(ref _replicas);

    public static void Set(int replicas, long memoryBytes)
    {
        Volatile.Write(ref _replicas, replicas);
        Interlocked.Exchange(ref _memory, memoryBytes);
    }

    /// <summary>Reads connected_slaves and used_memory from the primary's INFO.</summary>
    public static async Task<(int Replicas, long Memory)> ReadAsync(IConnectionMultiplexer redis)
    {
        foreach (var server in redis.GetServers().Where(s => s.IsConnected && !s.IsReplica))
        {
            var info = await server.InfoAsync();
            var all = info.SelectMany(g => g).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var replicas = all.TryGetValue("connected_slaves", out var c) && int.TryParse(c, out var n) ? n : 0;
            var memory = all.TryGetValue("used_memory", out var m) && long.TryParse(m, out var b) ? b : 0;
            return (replicas, memory);
        }

        return (0, 0);
    }
}

/// <summary>Polls Redis INFO every few seconds for <see cref="RedisMetrics"/>.</summary>
public sealed class RedisInfoMonitor(IConnectionMultiplexer redis, ILogger<RedisInfoMonitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (replicas, memory) = await RedisMetrics.ReadAsync(redis);
                RedisMetrics.Set(replicas, memory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RedisMetrics.Set(0, 0);
                log.LogDebug(ex, "Could not read Redis INFO");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>Unhealthy when a replica is required but the primary has none (spec 09 R4).</summary>
public sealed class RedisReplicaHealthCheck(IConnectionMultiplexer redis, Redis.RedisOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.RequireReplicaAck)
        {
            return HealthCheckResult.Healthy("replica not required");
        }

        try
        {
            var (replicas, _) = await RedisMetrics.ReadAsync(redis);
            return replicas > 0 ? HealthCheckResult.Healthy("replica connected") : HealthCheckResult.Unhealthy("Redis has no connected replica");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("could not read Redis INFO", ex);
        }
    }
}