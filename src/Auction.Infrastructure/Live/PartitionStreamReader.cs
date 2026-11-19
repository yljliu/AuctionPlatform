using System.Diagnostics.Metrics;
using Auction.Infrastructure.Bidding.Persistence;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Live;

/// <summary>Live metrics of the Api (meter "Auction.Live").</summary>
public static class LiveMetrics
{
    public const string MeterName = "Auction.Live";

    private static readonly Meter Meter = new(MeterName);
    private static AuctionEventBus? _bus;

    /// <summary>live_read_delay_ms: Redis script time to this Api copy reading the entry.</summary>
    public static readonly Histogram<double> ReadDelay = Meter.CreateHistogram<double>("live_read_delay_ms");

    static LiveMetrics()
    {
        Meter.CreateObservableGauge("live_subscribers", () => _bus?.SubscriberCount ?? 0);
    }

    /// <summary>The bus whose subscribers the live_subscribers gauge counts.</summary>
    public static void Track(AuctionEventBus bus) => _bus = bus;
}

/// <summary>
/// Reads every new entry of one partition stream and publishes the public ones to the bus
/// (spec 04 design 2.1). No consumer group: every Api copy reads everything.
/// StackExchange.Redis has no blocking XREAD, so the caller polls ReadOnceAsync.
/// </summary>
public sealed class PartitionStreamReader(
    IConnectionMultiplexer redis,
    AuctionEventBus bus,
    UserHandleCache handles,
    TimeProvider clock,
    ILogger<PartitionStreamReader> log,
    int partition)
{
    public const int BatchSize = 500;

    private readonly string _stream = $"{{p{partition}}}:stream";

    /// <summary>The id of the last entry read. Reading goes on from here after an error.</summary>
    public string? LastId { get; private set; }

    /// <summary>Starts at the end of the stream: only entries added from now on (like XREAD $).</summary>
    public async Task StartAsync()
    {
        try
        {
            var info = await redis.GetDatabase().StreamInfoAsync(_stream);
            LastId = info.LastGeneratedId.ToString();
        }
        catch (RedisServerException ex) when (ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
        {
            LastId = "0-0";   // the stream does not exist yet: everything that comes is new
        }
    }

    /// <summary>Reads up to 500 entries after LastId and publishes them. Returns how many were read.</summary>
    public async Task<int> ReadOnceAsync(CancellationToken ct = default)
    {
        if (LastId == null)
        {
            await StartAsync();
        }

        var entries = await redis.GetDatabase().StreamReadAsync(_stream, LastId!, BatchSize);
        if (entries.Length == 0)
        {
            return 0;
        }

        var parsed = new List<(PersistEntry Entry, Dictionary<string, string> Fields)>();
        foreach (var entry in entries)
        {
            var fields = new Dictionary<string, string>();
            foreach (var value in entry.Values)
            {
                fields[value.Name!] = value.Value.ToString();
            }

            try
            {
                parsed.Add((PersistEntryParser.Parse(entry.Id!, fields), fields));
            }
            catch (FormatException ex)
            {
                log.LogWarning(ex, "Skipping unreadable stream entry {Id}", entry.Id);
            }
        }

        // One handle lookup for the whole batch.
        var users = parsed.Where(p => p.Entry.Seq != null).SelectMany(p => LiveEventMapper.UsersIn(p.Entry));
        var masked = await handles.GetMaskedAsync(users, ct);

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var (entry, fields) in parsed)
        {
            var live = LiveEventMapper.Map(entry, fields, masked);
            if (live == null)
            {
                continue;   // MaxRaised: private
            }

            LiveMetrics.ReadDelay.Record(Math.Max(0, now - entry.AtMs));
            bus.Publish(entry.AuctionId, live);
        }

        LastId = entries[^1].Id.ToString();
        return entries.Length;
    }
}
