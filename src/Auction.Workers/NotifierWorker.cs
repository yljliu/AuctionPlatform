using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Notify;
using Auction.Infrastructure.Redis;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;

namespace Auction.Workers;

/// <summary>
/// Runs the notifier on every partition stream and on the outbox stream (spec 07 task 3).
/// Every workers copy joins the same group, so each entry goes to one copy.
/// </summary>
public sealed class NotifierWorker(
    IConnectionMultiplexer redis,
    NotificationStore store,
    RedisOptions redisOptions,
    PersisterOptions persisterOptions,
    ILogger<Notifier> notifierLog,
    ILogger<NotifierWorker> log) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(100);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The consumer name is this copy's name, so its own pending entries come back to it after a restart.
        var consumer = persisterOptions.Consumer;
        var notifiers = Enumerable.Range(0, redisOptions.Partitions)
            .Select(p => Notifier.ForPartition(redis, store, notifierLog, p, consumer))
            .Append(Notifier.ForOutbox(redis, store, notifierLog, consumer));
        return Task.WhenAll(notifiers.Select(n => RunAsync(n, stoppingToken)));
    }

    private async Task RunAsync(Notifier notifier, CancellationToken ct)
    {
        var started = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!started)
                {
                    await notifier.StartAsync();
                    started = true;
                }

                if (await notifier.RunOnceAsync(ct) == 0)
                {
                    await Task.Delay(Idle, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException or SqlException or TimeoutException)
            {
                // Not acknowledged, so the entry is read again; event keys stop repeats.
                log.LogWarning(ex, "Notifier on {Stream} failed; retrying in 1 s", notifier.Stream);
                started = false;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
