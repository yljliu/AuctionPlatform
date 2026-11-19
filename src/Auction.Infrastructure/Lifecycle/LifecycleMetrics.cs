using System.Diagnostics.Metrics;

namespace Auction.Infrastructure.Lifecycle;

/// <summary>Lifecycle metrics (spec 05 R5.2): how long Sealed auctions wait to settle, and how many settle.</summary>
public static class LifecycleMetrics
{
    public const string MeterName = "Auction.Lifecycle";

    private static readonly Meter Meter = new(MeterName);
    private static double _settlementLagSeconds;
    private static LeaseManager? _leader;

    /// <summary>The lease manager whose IsLeader feeds lifecycle_is_leader (one per process).</summary>
    public static void Track(LeaseManager lease) => Volatile.Write(ref _leader, lease);

    public static readonly Counter<long> Settled = Meter.CreateCounter<long>("auctions_settled", description: "Auctions settled by the lifecycle leader");

    static LifecycleMetrics()
    {
        Meter.CreateObservableGauge("settlement_lag_seconds", () => Volatile.Read(ref _settlementLagSeconds),
            description: "Longest time a Sealed auction has waited for its bids to be written to SQL");
        Meter.CreateObservableGauge("lifecycle_is_leader", () => Volatile.Read(ref _leader) is { } lease && lease.IsLeader ? 1 : 0,
            description: "1 when this copy holds the lifecycle lease");
    }

    public static double SettlementLagSeconds => Volatile.Read(ref _settlementLagSeconds);

    public static void SetSettlementLag(double seconds) => Volatile.Write(ref _settlementLagSeconds, seconds);
}
