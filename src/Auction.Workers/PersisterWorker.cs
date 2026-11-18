using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Redis;
using StackExchange.Redis;

namespace Auction.Workers;

/// <summary>
/// Runs the stream persister for every partition (MVP: one). Also measures persist_lag_ms every
/// second and trims the stream every 10 seconds (spec 03 design 6, task 9).
/// </summary>
public sealed class PersisterWorker(
    IConnectionMultiplexer redis,
    ScriptRegistry scripts,
    PersistSqlWriter sql,
    PersisterOptions options,
    RedisOptions redisOptions,
    TimeProvider clock,
    ILoggerFactory loggers,
    ILogger<PersisterWorker> log) : BackgroundService
{
    private static readonly TimeSpan LagEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TrimEvery = TimeSpan.FromSeconds(10);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, redisOptions.Partitions).Select(p => RunPartitionAsync(p, stoppingToken)));

    private async Task RunPartitionAsync(int partition, CancellationToken ct)
    {
        var persister = new StreamPersister(redis, scripts, sql, options, loggers.CreateLogger<StreamPersister>(), clock, partition);
        var lastLag = DateTimeOffset.MinValue;
        var lastTrim = DateTimeOffset.MinValue;
        var started = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!started)
                {
                    await persister.StartAsync();
                    started = true;
                    log.LogInformation("Persister started for partition {Partition} as {Consumer}", partition, options.Consumer);
                }

                var round = await persister.RunOnceAsync(ct);

                var now = clock.GetUtcNow();
                if (now - lastLag >= LagEvery)
                {
                    await persister.MeasureLagAsync();
                    lastLag = now;
                }

                if (now - lastTrim >= TrimEvery)
                {
                    await persister.TrimAsync();
                    lastTrim = now;
                }

                if (round.WaitBefore > TimeSpan.Zero)
                {
                    await Task.Delay(round.WaitBefore, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                // Redis down: nothing was acknowledged, so nothing is lost. Start over when it is back.
                log.LogWarning(ex, "Redis unavailable for partition {Partition}; retrying in 1 s", partition);
                started = false;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
