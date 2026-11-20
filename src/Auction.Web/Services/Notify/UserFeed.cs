using Auction.Contracts;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Auction.Web.Services.Notify;

/// <summary>The Api calls the bell needs, behind an interface so UserFeed can be tested without the Api.</summary>
public interface IUserFeedSource
{
    Task<ListNotificationsResponse> LatestAsync(int count, CancellationToken ct);

    IAsyncEnumerable<UserEvent> SubscribeAsync(CancellationToken ct);

    Task<int> MarkReadAsync(long id, CancellationToken ct);

    Task<int> MarkAllReadAsync(CancellationToken ct);
}

public sealed class GrpcUserFeedSource(NotificationService.NotificationServiceClient client) : IUserFeedSource
{
    public async Task<ListNotificationsResponse> LatestAsync(int count, CancellationToken ct) =>
        await client.ListNotificationsAsync(new ListNotificationsRequest { PageSize = count }, cancellationToken: ct);

    public async IAsyncEnumerable<UserEvent> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var call = client.SubscribeUser(new SubscribeUserRequest(), cancellationToken: ct);
        await foreach (var e in call.ResponseStream.ReadAllAsync(ct))
        {
            yield return e;
        }
    }

    public async Task<int> MarkReadAsync(long id, CancellationToken ct) =>
        (await client.MarkReadAsync(new MarkReadRequest { NotificationIds = { id } }, cancellationToken: ct)).Unread;

    public async Task<int> MarkAllReadAsync(CancellationToken ct) =>
        (await client.MarkAllReadAsync(new Empty(), cancellationToken: ct)).Unread;
}

/// <summary>
/// The bell for one browser connection (scoped, spec 07 design 3): unread count, the latest 10, and each new
/// notification as it arrives (R3.1). Reconnects with backoff and reloads on reconnect, so missed ones show (R3.2).
/// An auction page calls SuppressFor while open: outbid notices for it count, but raise no popup (R3.3).
/// </summary>
public sealed class UserFeed(IUserFeedSource source, ILogger<UserFeed> log) : IAsyncDisposable, IDisposable
{
    public const int LatestCount = 10;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _suppressed = [];
    private readonly CancellationTokenSource _stop = new();
    private List<NotificationItem> _latest = [];
    private Task? _loop;
    private int _disposed;

    public int Unread { get; private set; }

    public IReadOnlyList<NotificationItem> Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest.ToList();
            }
        }
    }

    /// <summary>Delays between reconnects: 0.5 s doubling to 10 s.</summary>
    public Func<int, TimeSpan> Backoff { get; set; } = attempt => TimeSpan.FromMilliseconds(Math.Min(10_000, 500 << Math.Min(attempt, 5)));

    /// <summary>Raised on the feed's thread. The item is the new notification (null for a reload or a read change); popup says whether to show it.</summary>
    public event Action<NotificationItem?, bool>? Changed;

    /// <summary>Starts once per connection; later calls do nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _loop ??= Task.Run(() => RunAsync(_stop.Token));
        }
    }

    /// <summary>While the returned handle lives, outbid notices for this auction raise no popup.</summary>
    public IDisposable SuppressFor(Guid auctionId)
    {
        lock (_gate)
        {
            _suppressed[auctionId] = _suppressed.GetValueOrDefault(auctionId) + 1;
        }

        return new Release(this, auctionId);
    }

    public bool IsSuppressed(Guid auctionId)
    {
        lock (_gate)
        {
            return _suppressed.ContainsKey(auctionId);
        }
    }

    public async Task MarkReadAsync(long id)
    {
        Unread = await source.MarkReadAsync(id, _stop.Token);
        lock (_gate)
        {
            _latest = _latest.Select(n => n.NotificationId == id ? WithRead(n) : n).ToList();
        }

        Changed?.Invoke(null, false);
    }

    public async Task MarkAllReadAsync()
    {
        Unread = await source.MarkAllReadAsync(_stop.Token);
        lock (_gate)
        {
            _latest = _latest.Select(WithRead).ToList();
        }

        Changed?.Invoke(null, false);
    }

    /// <summary>One live notification arrived.</summary>
    public void Receive(NotificationItem item)
    {
        bool popup;
        lock (_gate)
        {
            if (_latest.Any(n => n.NotificationId == item.NotificationId))
            {
                return;   // already shown (seen in the reload)
            }

            _latest.Insert(0, item);
            if (_latest.Count > LatestCount)
            {
                _latest.RemoveAt(_latest.Count - 1);
            }

            Unread++;
            popup = !(item.Type == "Outbid" && Guid.TryParse(item.AuctionId, out var auction) && _suppressed.ContainsKey(auction));
        }

        Changed?.Invoke(item, popup);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReloadAsync(ct);
                await foreach (var e in source.SubscribeAsync(ct))
                {
                    attempt = 0;
                    if (e.BodyCase == UserEvent.BodyOneofCase.Notification)
                    {
                        Receive(e.Notification);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                log.LogInformation("Bell feed stopped: signed out");
                return;
            }
            catch (Exception ex) when (ex is RpcException or IOException or InvalidOperationException)
            {
                log.LogDebug(ex, "Bell feed broke; reconnecting");
            }

            try
            {
                await Task.Delay(Backoff(attempt++), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // On every (re)connect: the true count and the latest 10, so nothing missed stays hidden (R3.2).
    private async Task ReloadAsync(CancellationToken ct)
    {
        var page = await source.LatestAsync(LatestCount, ct);
        lock (_gate)
        {
            _latest = page.Items.ToList();
            Unread = page.Unread;
        }

        Changed?.Invoke(null, false);
    }

    private static NotificationItem WithRead(NotificationItem n)
    {
        var copy = n.Clone();
        copy.Read = true;
        return copy;
    }

    /// <summary>
    /// The error page disposes its scope the sync way; without this it crashes (and shows the bare error screen).
    /// Cancels the loop without waiting; DisposeAsync is still the normal path for a circuit.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stop.Cancel();
        if (_loop != null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private sealed class Release(UserFeed feed, Guid auctionId) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1)
            {
                return;
            }

            lock (feed._gate)
            {
                var n = feed._suppressed.GetValueOrDefault(auctionId) - 1;
                if (n <= 0)
                {
                    feed._suppressed.Remove(auctionId);
                }
                else
                {
                    feed._suppressed[auctionId] = n;
                }
            }
        }
    }
}
