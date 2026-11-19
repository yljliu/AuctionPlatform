using Auction.Infrastructure.Redis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using ProtoAuctionEvent = Auction.Contracts.AuctionEvent;

namespace Auction.Infrastructure.Live;

public sealed class LiveOptions
{
    /// <summary>Heartbeat every 15 s when nothing else is sent (R1.4).</summary>
    public TimeSpan HeartbeatEvery { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Runs one SubscribeAuction call (spec 04 design 2.3):
/// 1. subscribe to the bus first, so nothing published from now on is missed;
/// 2. send a snapshot, or the kept events after last_seen;
/// 3. send a heartbeat as the "caught up" mark;
/// 4. send live events in seq order, dropping repeats, resyncing on a gap or a lagged queue,
///    with a heartbeat whenever 15 s pass quietly.
/// </summary>
public sealed class AuctionStreamer(
    AuctionEventBus bus,
    SnapshotBuilder snapshots,
    UserHandleCache handles,
    IConnectionMultiplexer redis,
    RedisOptions redisOptions,
    LiveOptions options,
    TimeProvider clock,
    ILogger<AuctionStreamer> log)
{
    public async Task RunAsync(Guid auctionId, long lastSeen, Func<ProtoAuctionEvent, Task> write, CancellationToken ct)
    {
        using var subscription = bus.Subscribe(auctionId);                          // 1. register first

        var sent = await StartAsync(auctionId, lastSeen, write, ct);               // 2. snapshot or replay
        await write(Heartbeat(auctionId, sent));                                     // 3. caught up

        while (!ct.IsCancellationRequested)                                         // 4. live
        {
            using var quiet = new CancellationTokenSource(options.HeartbeatEvery, clock);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, quiet.Token);
            bool more;
            try
            {
                more = await subscription.Reader.WaitToReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await write(Heartbeat(auctionId, sent));
                continue;
            }

            if (!more)
            {
                return;   // the subscription was closed
            }

            if (subscription.TakeLagged())
            {
                log.LogInformation("Subscriber of {AuctionId} fell behind; sending a snapshot", auctionId);
                sent = await SendSnapshotAsync(auctionId, write, ct);
            }

            while (subscription.Reader.TryRead(out var next))
            {
                switch (LiveSequencer.Next(sent, next.Seq))
                {
                    case LiveStep.Send:
                        await write(next);
                        sent = next.Seq;
                        break;
                    case LiveStep.Resync:
                        sent = await SendSnapshotAsync(auctionId, write, ct);
                        break;
                    default:
                        break;   // already sent
                }
            }
        }
    }

    /// <summary>Returns the last seq sent.</summary>
    private async Task<long> StartAsync(Guid auctionId, long lastSeen, Func<ProtoAuctionEvent, Task> write, CancellationToken ct)
    {
        var keys = RedisKeys.For(auctionId, redisOptions);
        long? currentSeq = null;
        long? oldestKept = null;
        var currency = "USD";

        try
        {
            var database = redis.GetDatabase();
            var state = await database.HashGetAsync(keys.State, ["seq", "cur", "inc"]);
            if (!state[2].IsNullOrEmpty)
            {
                currentSeq = (long)state[0];
                currency = state[1].IsNullOrEmpty ? "USD" : state[1].ToString();
                var first = await database.SortedSetRangeByRankWithScoresAsync(keys.Events, 0, 0);
                oldestKept = first.Length == 0 ? null : (long)first[0].Score;
            }
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogWarning(ex, "Redis unavailable at subscribe of {AuctionId}; sending a snapshot", auctionId);
        }

        if (LiveSequencer.ChooseStart(lastSeen, currentSeq, oldestKept) == StartKind.Snapshot)
        {
            return await SendSnapshotAsync(auctionId, write, ct);
        }

        // Replay the kept events after last_seen.
        var members = await redis.GetDatabase().SortedSetRangeByScoreAsync(keys.Events, lastSeen, double.PositiveInfinity, Exclude.Start);
        var stored = members.Select(m => StoredEventMapper.Parse(m.ToString())).ToList();
        if (stored.Any(s => s == null) || !LiveSequencer.IsUnbroken(lastSeen, stored.Select(s => s!.Seq).ToList()))
        {
            return await SendSnapshotAsync(auctionId, write, ct);   // a hole or a bad row: start fresh
        }

        var masked = await handles.GetMaskedAsync(stored.SelectMany(s => StoredEventMapper.UsersIn(s!)), ct);
        var sent = lastSeen;
        foreach (var item in stored)
        {
            await write(StoredEventMapper.Map(auctionId, item!, currency, masked));
            sent = item!.Seq;
        }

        return sent;
    }

    private async Task<long> SendSnapshotAsync(Guid auctionId, Func<ProtoAuctionEvent, Task> write, CancellationToken ct)
    {
        var snapshot = await snapshots.BuildAsync(auctionId, ct);
        await write(new ProtoAuctionEvent
        {
            Seq = snapshot.Seq,
            ServerTsMs = snapshot.ServerTsMs,
            AuctionId = auctionId.ToString(),
            Snapshot = snapshot,
        });
        return snapshot.Seq;
    }

    private ProtoAuctionEvent Heartbeat(Guid auctionId, long sent) => new()
    {
        Seq = sent,
        ServerTsMs = clock.GetUtcNow().ToUnixTimeMilliseconds(),
        AuctionId = auctionId.ToString(),
        Heartbeat = new Contracts.Heartbeat(),
    };
}
