using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using ProtoAuctionEvent = Auction.Contracts.AuctionEvent;

namespace Auction.Infrastructure.Live;

/// <summary>
/// One subscriber's queue of events for one auction. Bounded: when it is full the oldest event is
/// dropped and Lagged is set, so the caller knows to send a fresh snapshot (design 2.2).
/// </summary>
public sealed class AuctionSubscription : IDisposable
{
    public const int Capacity = 1000;

    private readonly Action<AuctionSubscription> _onDispose;
    private int _lagged;
    private int _disposed;

    internal AuctionSubscription(Guid auctionId, Action<AuctionSubscription> onDispose)
    {
        AuctionId = auctionId;
        _onDispose = onDispose;
        Channel = System.Threading.Channels.Channel.CreateBounded<ProtoAuctionEvent>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Exchange(ref _lagged, 1));
    }

    public Guid AuctionId { get; }

    internal Channel<ProtoAuctionEvent> Channel { get; }

    public ChannelReader<ProtoAuctionEvent> Reader => Channel.Reader;

    /// <summary>True once events were dropped since the last call; reading it clears it.</summary>
    public bool TakeLagged() => Interlocked.Exchange(ref _lagged, 0) == 1;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _onDispose(this);
            Channel.Writer.TryComplete();
        }
    }
}

/// <summary>
/// In-process fan-out of live events inside one Api copy: the stream reader publishes, each
/// SubscribeAuction call reads its own queue (design 2.2). Publishing never waits.
/// </summary>
public sealed class AuctionEventBus
{
    private readonly ConcurrentDictionary<Guid, ImmutableList<AuctionSubscription>> _subscribers = new();

    /// <summary>live_subscribers: how many open subscriptions this Api copy has.</summary>
    public int SubscriberCount => _subscribers.Values.Sum(list => list.Count);

    public AuctionSubscription Subscribe(Guid auctionId)
    {
        var subscription = new AuctionSubscription(auctionId, Remove);
        _subscribers.AddOrUpdate(auctionId, _ => [subscription], (_, list) => list.Add(subscription));
        return subscription;
    }

    /// <summary>Hands the event to every subscriber of its auction. Returns how many got it.</summary>
    public int Publish(Guid auctionId, ProtoAuctionEvent liveEvent)
    {
        if (!_subscribers.TryGetValue(auctionId, out var list))
        {
            return 0;
        }

        var delivered = 0;
        foreach (var subscription in list)
        {
            if (subscription.Channel.Writer.TryWrite(liveEvent))
            {
                delivered++;
            }
        }

        return delivered;
    }

    private void Remove(AuctionSubscription subscription)
    {
        while (_subscribers.TryGetValue(subscription.AuctionId, out var list))
        {
            var next = list.Remove(subscription);
            var done = next.IsEmpty
                ? _subscribers.TryRemove(new KeyValuePair<Guid, ImmutableList<AuctionSubscription>>(subscription.AuctionId, list))
                : _subscribers.TryUpdate(subscription.AuctionId, next, list);
            if (done)
            {
                return;
            }
        }
    }
}
