using Auction.Web.Services.Live;
using FluentAssertions;
using Microsoft.JSInterop;
using Xunit;

namespace Auction.Web.Tests;

/// <summary>Spec 04 task 7: the browser clock offset is a moving average with factor 0.2 (R6.1).</summary>
public class ServerClockTests
{
    [Fact]
    public void First_sample_is_taken_as_is()
    {
        var offset = new ClockOffset();

        offset.Add(serverTsMs: 10_500, browserNowMs: 10_000).Should().Be(500);
        offset.HasSample.Should().BeTrue();
    }

    [Fact]
    public void Later_samples_move_the_offset_by_one_fifth()
    {
        var offset = new ClockOffset();
        offset.Add(10_500, 10_000);                       // 500

        offset.Add(20_000, 19_000).Should().BeApproximately(600, 0.001);      // 0.8*500 + 0.2*1000
        offset.Add(30_000, 30_000).Should().BeApproximately(480, 0.001);      // 0.8*600 + 0.2*0
    }

    [Fact]
    public void Browser_ahead_of_server_gives_a_negative_offset()
    {
        var offset = new ClockOffset();

        offset.Add(10_000, 12_000).Should().Be(-2000);
    }

    [Fact]
    public void One_odd_sample_moves_the_offset_only_a_little()
    {
        var offset = new ClockOffset();
        offset.Add(1_000, 1_000);                          // 0

        offset.Add(5_000, 0).Should().BeApproximately(1000, 0.001, "a 5 s network hiccup moves the clock by 1 s, not 5 s");
    }

    [Fact]
    public async Task Observe_reads_browser_time_and_pushes_the_offset()
    {
        var js = new FakeJs(browserNowMs: 9_700);
        var clock = new ServerClock(js);

        await clock.ObserveAsync(10_000);

        js.Calls.Should().Equal("auctionClock.nowMs", "auctionClock.setOffset");
        js.LastOffset.Should().Be(300);
        clock.OffsetMs.Should().Be(300);
    }

    [Fact]
    public async Task Same_or_older_server_time_is_ignored()
    {
        var js = new FakeJs(browserNowMs: 9_700);
        var clock = new ServerClock(js);
        await clock.ObserveAsync(10_000);
        js.Calls.Clear();

        await clock.ObserveAsync(10_000);
        await clock.ObserveAsync(9_000);
        await clock.ObserveAsync(0);

        js.Calls.Should().BeEmpty("a redraw with no new server time needs no new sample");
    }

    private sealed class FakeJs(long browserNowMs) : IJSRuntime
    {
        public List<string> Calls { get; } = [];

        public double LastOffset { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add(identifier);
            if (identifier == "auctionClock.setOffset")
            {
                LastOffset = Convert.ToDouble(args![0]);
                return ValueTask.FromResult(default(TValue)!);
            }

            return ValueTask.FromResult((TValue)(object)browserNowMs);
        }
    }
}
