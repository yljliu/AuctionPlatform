using Auction.Infrastructure.Redis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Notify;

/// <summary>
/// Reads one stream with the consumer group "notify" (spec 07 design 2): a partition stream {pN}:stream
/// or the "outbox" stream. Entries are acknowledged only after their notifications are saved, so a restart
/// re-reads the unacknowledged ones first; event keys make that harmless (R4.1).
/// </summary>
public sealed class Notifier(IConnectionMultiplexer redis, NotificationStore store, ILogger<Notifier> log, string stream, string consumer)
{
    public const string Group = "notify";
    public const int BatchSize = 100;

    private IDatabase Db => redis.GetDatabase();

    public string Stream => stream;

    /// <summary>For a partition stream.</summary>
    public static Notifier ForPartition(IConnectionMultiplexer redis, NotificationStore store, ILogger<Notifier> log, int partition, string consumer) =>
        new(redis, store, log, new PartitionKeys(partition).Tag + ":stream", consumer);

    public static Notifier ForOutbox(IConnectionMultiplexer redis, NotificationStore store, ILogger<Notifier> log, string consumer, string stream = OutboxRelay.Stream) =>
        new(redis, store, log, stream, consumer) { IsOutbox = true };

    /// <summary>True for the outbox stream (its entries are outbox rows, not bids and states).</summary>
    public bool IsOutbox { get; private init; }

    /// <summary>
    /// Creates the group if needed. The outbox group starts at the beginning, so no settlement is missed;
    /// a partition group starts at the end, so old bids from before the notifier existed are not announced.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            await Db.StreamCreateConsumerGroupAsync(stream, Group, IsOutbox ? "0-0" : "$", createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
        {
            // already there
        }
    }

    /// <summary>One round: first this consumer's unacknowledged entries (after a restart), then new ones. Returns how many were handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var entries = await Db.StreamReadGroupAsync(stream, Group, consumer, "0-0", BatchSize);
        if (entries.Length == 0)
        {
            entries = await Db.StreamReadGroupAsync(stream, Group, consumer, ">", BatchSize);
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            await HandleAsync(entry, ct);
            await Db.StreamAcknowledgeAsync(stream, Group, entry.Id);
        }

        return entries.Length;
    }

    private async Task HandleAsync(StreamEntry entry, CancellationToken ct)
    {
        var fields = entry.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());
        var e = IsOutbox
            ? NotificationPlanner.FromOutbox(fields.GetValueOrDefault("topic") ?? "", fields.GetValueOrDefault("payload") ?? "{}")
            : NotificationPlanner.FromStream(fields);
        if (e == null)
        {
            return;
        }

        var auction = await store.AuctionAsync(e.AuctionId, ct);
        if (auction == null)
        {
            log.LogWarning("Notifier skipped entry {Id}: auction {AuctionId} is not in SQL", entry.Id, e.AuctionId);
            return;
        }

        var who = NotificationPlanner.NeedsRecipients(e)
            ? await store.RecipientsAsync(e.AuctionId, ct)
            : new Recipients(new HashSet<Guid>(), new HashSet<Guid>());
        var drafts = NotificationPlanner.Plan(e, auction, who);
        var added = await store.SaveAndPublishAsync(drafts, ct);
        if (added.Count > 0)
        {
            NotifyMetrics.Created.Add(added.Count);
            log.LogDebug("Notifier {Stream} entry {Id}: {Count} notifications", stream, entry.Id, added.Count);
        }
    }
}

public static class NotifyMetrics
{
    public const string MeterName = "Auction.Notify";

    private static readonly System.Diagnostics.Metrics.Meter Meter = new(MeterName);

    public static readonly System.Diagnostics.Metrics.Counter<long> Created = Meter.CreateCounter<long>("notifications_created");
}
