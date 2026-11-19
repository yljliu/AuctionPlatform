using Auction.Infrastructure.Notify;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;

namespace Auction.Workers;

/// <summary>Runs the outbox relay (spec 07 task 2): every 200 ms, or at once again after a full batch. Every copy runs it.</summary>
public sealed class OutboxRelayWorker(OutboxRelay relay, ILogger<OutboxRelayWorker> log) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(200);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await relay.RunOnceAsync(stoppingToken);
                if (sent < OutboxRelay.BatchSize)
                {
                    await Task.Delay(Idle, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is SqlException or RedisException or RedisTimeoutException or TimeoutException)
            {
                // Nothing was marked as sent, so the rows go again next time.
                log.LogWarning(ex, "Outbox relay failed; retrying in 1 s");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
