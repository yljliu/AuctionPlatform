using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Auction.Web.Services.Admin;

/// <summary>
/// Counts open browser connections (Blazor circuits) for the "online users" tile (spec 08 design 3):
/// gauge circuits_active, summed over web copies by Prometheus.
/// </summary>
public sealed class CircuitCounter : CircuitHandler
{
    public const string MeterName = "Auction.Web.Circuits";

    private static readonly Meter Meter = new(MeterName);
    private static int _open;

    static CircuitCounter()
    {
        Meter.CreateObservableGauge("circuits_active", () => Volatile.Read(ref _open), description: "Open browser connections on this web copy");
    }

    public static int Open => Volatile.Read(ref _open);

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _open);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Interlocked.Decrement(ref _open);
        return Task.CompletedTask;
    }
}

/// <summary>Whether a simulator address is set (the admin panel hides its form when not).</summary>
public sealed record SimulatorSettings(bool Configured);
