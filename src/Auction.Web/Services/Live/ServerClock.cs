using Microsoft.JSInterop;

namespace Auction.Web.Services.Live;

/// <summary>
/// The moving-average math for the browser clock offset (R6.1).
/// offset = how far the server clock is ahead of the browser clock, in ms.
/// </summary>
public sealed class ClockOffset
{
    public const double Factor = 0.2;

    public bool HasSample { get; private set; }

    public double OffsetMs { get; private set; }

    /// <summary>Adds one sample: server_ts minus the browser's Date.now() when the event arrived.</summary>
    /// <remarks>The first sample is taken as is, so the countdown is right at once instead of after many events.</remarks>
    public double Add(long serverTsMs, long browserNowMs)
    {
        double sample = serverTsMs - browserNowMs;
        OffsetMs = HasSample ? ((1 - Factor) * OffsetMs) + (Factor * sample) : sample;
        HasSample = true;
        return OffsetMs;
    }
}

/// <summary>
/// One per browser connection (scoped). Reads the browser time, keeps the offset and pushes it to clock.js.
/// The countdown itself ticks in the browser, so the server does no per-second work.
/// </summary>
public sealed class ServerClock(IJSRuntime js)
{
    private readonly ClockOffset _offset = new();
    private long _lastServerTsMs;

    public double OffsetMs => _offset.OffsetMs;

    /// <summary>Takes a new sample when the server time moved on. Call after render (JS is ready then).</summary>
    public async Task ObserveAsync(long serverTsMs)
    {
        if (serverTsMs <= 0 || serverTsMs <= _lastServerTsMs)
        {
            return;   // nothing new: the same snapshot, event or heartbeat
        }

        _lastServerTsMs = serverTsMs;
        var browserNow = await js.InvokeAsync<long>("auctionClock.nowMs");
        var offset = _offset.Add(serverTsMs, browserNow);
        await js.InvokeVoidAsync("auctionClock.setOffset", Math.Round(offset));
    }
}
