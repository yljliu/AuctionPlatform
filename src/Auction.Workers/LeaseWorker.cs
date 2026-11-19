using Auction.Infrastructure.Lifecycle;

namespace Auction.Workers;

/// <summary>Keeps trying for the lifecycle lease and renews it while held (spec 05 task 1).</summary>
public sealed class LeaseWorker(LeaseManager lease) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => lease.RunAsync(stoppingToken);
}
