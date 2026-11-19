using System.Collections.Concurrent;
using System.Threading.Channels;
using Auction.Infrastructure.Notify;
using StackExchange.Redis;

namespace Auction.Api.Live;

/// <summary>
/// Pub/Sub fan-out for the bell (spec 07 design 3). One Redis subscription per user channel u:{userId},
/// shared by all of that user's open streams on this Api copy; dropped when the last one closes.
/// </summary>
public sealed class UserEventHub(IConnectionMultiplexer redis, ILogger<UserEventHub> log)
{
    private sealed class UserChannel
    {
        public readonly object Gate = new();
        public readonly List<Channel<NotificationMessage>> Listeners = [];
        public Task? Subscribed;
        public Action<RedisChannel, RedisValue>? Handler;
    }

    private readonly ConcurrentDictionary<Guid, UserChannel> _users = new();

    public sealed class Subscription(UserEventHub hub, Guid userId, Channel<NotificationMessage> channel) : IAsyncDisposable
    {
        public ChannelReader<NotificationMessage> Reader => channel.Reader;

        public ValueTask DisposeAsync() => hub.RemoveAsync(userId, channel);
    }

    public int OpenStreams => _users.Values.Sum(u => u.Listeners.Count);

    public async Task<Subscription> SubscribeAsync(Guid userId)
    {
        var channel = Channel.CreateBounded<NotificationMessage>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        var user = _users.GetOrAdd(userId, _ => new UserChannel());
        Task subscribed;
        lock (user.Gate)
        {
            user.Listeners.Add(channel);
            if (user.Subscribed == null)
            {
                user.Handler = (_, value) => Deliver(userId, value);
                user.Subscribed = redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(NotificationStore.Channel(userId)), user.Handler);
            }

            subscribed = user.Subscribed;
        }

        await subscribed;
        return new Subscription(this, userId, channel);
    }

    private void Deliver(Guid userId, RedisValue value)
    {
        var message = NotificationMessage.FromJson(value.ToString());
        if (message == null || message.UserId != userId || !_users.TryGetValue(userId, out var user))
        {
            return;
        }

        lock (user.Gate)
        {
            foreach (var listener in user.Listeners)
            {
                listener.Writer.TryWrite(message);
            }
        }
    }

    private async ValueTask RemoveAsync(Guid userId, Channel<NotificationMessage> channel)
    {
        if (!_users.TryGetValue(userId, out var user))
        {
            return;
        }

        bool last;
        Action<RedisChannel, RedisValue>? handler = null;
        lock (user.Gate)
        {
            user.Listeners.Remove(channel);
            last = user.Listeners.Count == 0;
            if (last)
            {
                _users.TryRemove(userId, out _);
                user.Subscribed = null;
                handler = user.Handler;
            }
        }

        channel.Writer.TryComplete();
        if (last && handler != null)
        {
            try
            {
                // Only this handler: a new stream for the same user may already have its own.
                await redis.GetSubscriber().UnsubscribeAsync(RedisChannel.Literal(NotificationStore.Channel(userId)), handler);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                log.LogDebug(ex, "Unsubscribe failed for user {UserId}", userId);
            }
        }
    }
}
