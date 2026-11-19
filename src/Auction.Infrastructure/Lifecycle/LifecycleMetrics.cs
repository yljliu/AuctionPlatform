using System.Diagnostics.Metrics;

namespace Auction.Infrastructure.Lifecycle;

/// <summary>Lifecycle metrics (spec 05 R5.2): how long Sealed auctions wait to settle, and how many settle.</summary>
public static class LifecycleMetrics
{
    public const string MeterName = "Auction.Lifecycle";

    private static readonly Meter Meter = new(MeterName);
    private static double _settlementLagSeconds;

    public static readonly Counter<long> Settled = Meter.CreateCounter<long>("auctions_settled", description: "Auctions settled by the lifecycle leader");

    static LifecycleMetrics()
    {
        Meter.CreateObservableGauge("settlement_lag_seconds", () => Volatile.Read(ref _settlementLagSeconds),
            unit: "s", description: "Longest time a Sealed auction has waited for its bids to be written to SQL");
    }

    public static double SettlementLagSeconds => Volatile.Read(ref _settlementLagSeconds);

    public static void SetSettlementLag(double seconds) => Volatile.Write(ref _settlementLagSeconds, seconds);
}
