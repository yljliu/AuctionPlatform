using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Auction.Web.Services.Admin;
using Auction.Web.Services.Live;
using FluentAssertions;
using Xunit;

namespace Auction.Web.Tests;

/// <summary>Spec 09 task 3: the Web instruments keep the exact names the board and alerts read.</summary>
public class WebMetricNamesTests
{
    [Fact]
    public void Web_instruments_have_the_spec_names_and_no_unit()
    {
        RuntimeHelpers.RunClassConstructor(typeof(WebLiveMetrics).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(CircuitCounter).TypeHandle);

        var seen = new Dictionary<string, Instrument>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name.StartsWith("Auction.", StringComparison.Ordinal))
            {
                seen[instrument.Name] = instrument;
            }
        };
        listener.Start();

        seen.Keys.Should().Contain("live_fanout_delay_ms", "live_streams_open", "circuits_active");
        seen["live_fanout_delay_ms"].Unit.Should().BeNull();
    }
}