using Auction.Infrastructure.Live;
using Auction.Infrastructure.Redis;
using StackExchange.Redis;

namespace Auction.Api.Live;

/// <summary>
/// Runs one PartitionStreamReader per partition inside each Api copy (spec 04 design 2.1).
/// Polls every 20 ms when there is nothing new; after a Redis error it waits 1 s and goes on
/// from the last id it read, so nothing is skipped.
/// </summary>
public sealed class LiveStreamWorker(
    IConnectionMultiplexer redis,
    AuctionEventBus bus,
    UserHandleCache handles,
    RedisOptions options,
    TimeProvider clock,
    ILoggerFactory loggers,
    ILogger<LiveStreamWorker> log) : BackgroundService
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(20);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LiveMetrics.Track(bus);
        return Task.WhenAll(Enumerable.Range(0, options.Partitions).Select(p => RunAsync(p, stoppingToken)));
    }

    private async Task RunAsync(int partition, CancellationToken ct)
    {
        var reader = new PartitionStreamReader(redis, bus, handles, clock, loggers.CreateLogger<PartitionStreamReader>(), partition);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await reader.ReadOnceAsync(ct) == 0)
                {
                    await Task.Delay(IdleWait, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                log.LogWarning(ex, "Live reader for partition {Partition} lost Redis; resuming from {LastId} in 1 s", partition, reader.LastId);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
