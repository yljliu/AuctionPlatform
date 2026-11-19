using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using Auction.Contracts;

namespace Auction.Web.Services.Live;

public enum LiveFeedStatus
{
    /// <summary>First connect; nothing received yet.</summary>
    Connecting,

    /// <summary>Caught up and receiving live events. Bidding allowed.</summary>
    Live,

    /// <summary>The Api stream broke; trying again. Bidding paused (R4.4).</summary>
    Reconnecting,
}

/// <summary>What a watcher is told: the newest view and the feed status.</summary>
public sealed record LiveFeedUpdate(LiveAuctionView View, LiveFeedStatus Status);

public sealed class LiveHubOptions
{
    /// <summary>Close a feed this long after its last watcher left (R4.2).</summary>
    public TimeSpan IdleClose { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Wait after the n-th failure in a row: 0.2 s, 0.4 s ... at most 5 s (R4.4).</summary>
    public static TimeSpan Backoff(int failuresInARow) =>
        failuresInARow <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Math.Min(5000, 200 * Math.Pow(2, Math.Min(failuresInARow - 1, 10))));
}

/// <summary>Web live metrics (meter "Auction.WebLive").</summary>
public static class WebLiveMetrics
{
    public const string MeterName = "Auction.WebLive";

    private static readonly Meter Meter = new(MeterName);
    private static LiveAuctionHub? _hub;

    /// <summary>live_fanout_delay_ms: event time at the Api to this Web copy receiving it.</summary>
    public static readonly Histogram<double> FanoutDelay = Meter.CreateHistogram<double>("live_fanout_delay_ms", "ms");

    static WebLiveMetrics()
    {
        Meter.CreateObservableGauge("live_streams_open", () => _hub?.OpenStreams ?? 0);
    }

    public static void Track(LiveAuctionHub hub) => _hub = hub;
}

/// <summary>
/// One live stream per auction per Web copy, shared by every viewer on this copy (spec 04 design 3.1, R4.1).
/// A watcher gets the newest view at once, then every change. When the last watcher leaves,
/// the stream closes after 30 s unless someone comes back first.
/// </summary>
public sealed class LiveAuctionHub : IDisposable
{
    private readonly ILiveFeedSource _source;
    private readonly TimeProvider _clock;
    private readonly LiveHubOptions _options;
    private readonly ILogger<LiveAuctionHub> _log;
    private readonly Dictionary<Guid, LiveFeed> _feeds = [];
    private readonly object _gate = new();

    public LiveAuctionHub(ILiveFeedSource source, TimeProvider clock, LiveHubOptions options, ILogger<LiveAuctionHub> log)
    {
        _source = source;
        _clock = clock;
        _options = options;
        _log = log;
        WebLiveMetrics.Track(this);
    }

    /// <summary>live_streams_open: feeds with an open (or reconnecting) stream.</summary>
    public int OpenStreams
    {
        get
        {
            lock (_gate)
            {
                return _feeds.Count;
            }
        }
    }

    /// <summary>Starts watching. Dispose the result when the component goes away.</summary>
    public LiveWatch Watch(Guid auctionId, Action<LiveFeedUpdate> onUpdate)
    {
        LiveFeed feed;
        lock (_gate)
        {
            if (!_feeds.TryGetValue(auctionId, out feed!))
            {
                feed = new LiveFeed(auctionId, _source, _clock, _log);
                _feeds[auctionId] = feed;
                feed.Start();
            }

            feed.AddWatcher(onUpdate);
        }

        return new LiveWatch(this, feed, onUpdate);
    }

    internal void Release(LiveFeed feed, Action<LiveFeedUpdate> onUpdate)
    {
        lock (_gate)
        {
            if (feed.RemoveWatcher(onUpdate) == 0)
            {
                feed.StartIdleTimer(_clock, _options.IdleClose, () => CloseIfIdle(feed));
            }
        }
    }

    private void CloseIfIdle(LiveFeed feed)
    {
        lock (_gate)
        {
            if (feed.WatcherCount == 0 && _feeds.TryGetValue(feed.AuctionId, out var current) && current == feed)
            {
                _feeds.Remove(feed.AuctionId);
                feed.Stop();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var feed in _feeds.Values)
            {
                feed.Stop();
            }

            _feeds.Clear();
        }
    }
}

/// <summary>A watcher's handle. Current is the newest view; Dispose stops watching.</summary>
public sealed class LiveWatch : IDisposable
{
    private readonly LiveAuctionHub _hub;
    private readonly LiveFeed _feed;
    private readonly Action<LiveFeedUpdate> _onUpdate;
    private int _disposed;

    internal LiveWatch(LiveAuctionHub hub, LiveFeed feed, Action<LiveFeedUpdate> onUpdate)
    {
        _hub = hub;
        _feed = feed;
        _onUpdate = onUpdate;
    }

    public LiveFeedUpdate Current => _feed.Current;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _hub.Release(_feed, _onUpdate);
        }
    }
}

/// <summary>
/// One auction's stream. Applies every event to the shared view and tells the watchers.
/// On a break it reconnects with backoff from the last seq it has, so nothing is missed or repeated.
/// </summary>
internal sealed class LiveFeed(Guid auctionId, ILiveFeedSource source, TimeProvider clock, ILogger log)
{
    private readonly CancellationTokenSource _stop = new();
    private ImmutableList<Action<LiveFeedUpdate>> _watchers = [];
    private LiveFeedUpdate _current = new(LiveAuctionView.Empty(auctionId), LiveFeedStatus.Connecting);
    private ITimer? _idleTimer;

    public Guid AuctionId => auctionId;

    public LiveFeedUpdate Current => Volatile.Read(ref _current);

    public int WatcherCount => _watchers.Count;

    // Called under the hub lock.
    public void AddWatcher(Action<LiveFeedUpdate> onUpdate)
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        _watchers = _watchers.Add(onUpdate);
    }

    // Called under the hub lock.
    public int RemoveWatcher(Action<LiveFeedUpdate> onUpdate)
    {
        _watchers = _watchers.Remove(onUpdate);
        return _watchers.Count;
    }

    // Called under the hub lock.
    public void StartIdleTimer(TimeProvider timeProvider, TimeSpan after, Action onIdle)
    {
        _idleTimer?.Dispose();
        _idleTimer = timeProvider.CreateTimer(_ => onIdle(), null, after, Timeout.InfiniteTimeSpan);
    }

    public void Start() => _ = Task.Run(() => RunAsync(_stop.Token));

    public void Stop()
    {
        _idleTimer?.Dispose();
        _stop.Cancel();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var e in source.SubscribeAsync(auctionId, Current.View.Seq, ct))
                {
                    var status = Current.Status;
                    if (e.BodyCase is AuctionEvent.BodyOneofCase.Snapshot or AuctionEvent.BodyOneofCase.Heartbeat)
                    {
                        status = LiveFeedStatus.Live;   // a snapshot, or the "caught up" heartbeat
                        failures = 0;
                    }

                    if (e.ServerTsMs > 0)
                    {
                        WebLiveMetrics.FanoutDelay.Record(Math.Max(0, clock.GetUtcNow().ToUnixTimeMilliseconds() - e.ServerTsMs));
                    }

                    Publish(new LiveFeedUpdate(Current.View.Apply(e), status));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogInformation(ex, "Live stream for auction {AuctionId} broke; reconnecting", auctionId);
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            // A break, or the Api ended the stream: pause bidding and try again from the last seq.
            failures++;
            Publish(Current with { Status = LiveFeedStatus.Reconnecting });
            try
            {
                await Task.Delay(LiveHubOptions.Backoff(failures), clock, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Publish(LiveFeedUpdate update)
    {
        Volatile.Write(ref _current, update);
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher(update);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "A live watcher of {AuctionId} failed", auctionId);
            }
        }
    }
}
