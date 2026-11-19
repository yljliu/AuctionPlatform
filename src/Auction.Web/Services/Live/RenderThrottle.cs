namespace Auction.Web.Services.Live;

/// <summary>
/// Redraws at most once per interval (spec 04 design 3.2, R4.3): MarkDirty is cheap and may be called
/// on every event; a timer redraws only when something changed. Rows live in the view, so none are lost.
/// </summary>
public sealed class RenderThrottle : IDisposable
{
    public static readonly TimeSpan Default = TimeSpan.FromMilliseconds(100);   // 10 redraws a second

    private readonly Func<Task> _render;
    private readonly ITimer _timer;
    private int _dirty;

    public RenderThrottle(TimeSpan interval, TimeProvider clock, Func<Task> render)
    {
        _render = render;
        _timer = clock.CreateTimer(_ => Tick(), null, interval, interval);
    }

    public void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    private void Tick()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 1)
        {
            _ = _render();
        }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>Small display rules for the live panel. Pure, so they are unit tested.</summary>
public static class LiveDisplay
{
    /// <summary>Your own rows show "you"; everyone else stays masked (design 2.4).</summary>
    public static string NameFor(Guid bidderId, string maskedHandle, Guid? me) =>
        me != null && bidderId == me ? "you" : maskedHandle;

    /// <summary>Bidding only while the feed is caught up and the auction is Live (R4.4).</summary>
    public static bool CanBid(LiveFeedUpdate update) =>
        update.Status == LiveFeedStatus.Live && update.View.HasSnapshot && update.View.Status == "Live";
}
