using Auction.Infrastructure.Lifecycle;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;

namespace Auction.Workers;

/// <summary>
/// Runs the lifecycle tick every 250 ms, but only while this copy holds the lease (spec 05 tasks 3 and 4).
/// On becoming leader it first runs recovery (task 6). When the lease is lost the loop forgets what it
/// loaded, so the next leader start is clean.
/// </summary>
public sealed class LifecycleWorker(
    LeaseManager lease,
    LifecycleLoop loop,
    LifecycleOptions options,
    Auction.Infrastructure.Payments.DefaultSweeper defaults,
    TimeProvider clock,
    ILogger<LifecycleWorker> log) : BackgroundService
{
    /// <summary>Missed payments are checked once a minute (spec 10 R5.1).</summary>
    public static readonly TimeSpan DefaultsEvery = TimeSpan.FromMinutes(1);

    private DateTimeOffset _nextDefaults = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasLeader = false;
        var recovered = false;
        using var timer = new PeriodicTimer(options.TickEvery);

        while (await WaitAsync(timer, stoppingToken))
        {
            var grant = lease.Current;
            if (grant == null)
            {
                if (wasLeader)
                {
                    loop.Reset();
                    wasLeader = false;
                    recovered = false;
                    log.LogInformation("Lifecycle loop paused: this copy is no longer the leader");
                }

                continue;
            }

            if (!wasLeader)
            {
                wasLeader = true;
                log.LogInformation("Lifecycle loop running with fencing token {Token}", grant.Token);
            }

            try
            {
                if (!recovered)
                {
                    await loop.RecoverAsync(grant, stoppingToken);
                    recovered = true;
                }

                await loop.TickAsync(grant, stoppingToken);

                if (clock.GetUtcNow() >= _nextDefaults)
                {
                    _nextDefaults = clock.GetUtcNow() + DefaultsEvery;
                    await defaults.RunAsync(grant, ct: stoppingToken);
                    await defaults.ExpireOffersAsync(grant, stoppingToken);
                }
            }
            catch (StaleFencingTokenException ex)
            {
                // Another copy took over. Stop now; the lease manager will notice on its next renew.
                log.LogWarning(ex, "Lifecycle tick stopped: fencing token {Token} is stale", grant.Token);
                loop.Reset();
                wasLeader = false;
                recovered = false;
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException or SqlException or TimeoutException)
            {
                log.LogWarning(ex, "Lifecycle tick failed; trying again next tick");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
