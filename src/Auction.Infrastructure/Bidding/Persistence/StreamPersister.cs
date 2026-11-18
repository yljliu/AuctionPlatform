using System.Diagnostics.Metrics;
using Auction.Infrastructure.Redis;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Bidding.Persistence;

public sealed class PersisterOptions
{
    public const string Group = "persist";

    /// <summary>Consumer name in the group. One per worker copy.</summary>
    public string Consumer { get; set; } = Environment.MachineName;

    public int BatchSize { get; set; } = 200;

    /// <summary>Tries before an entry goes to the dead-letter stream (R8.5).</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Another consumer's entry idle this long is taken over (XAUTOCLAIM).</summary>
    public int ClaimIdleMs { get; set; } = 30_000;
}

/// <summary>persist_lag_ms gauge, one value per partition (meter "Auction.Persister").</summary>
public static class PersisterMetrics
{
    public const string MeterName = "Auction.Persister";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Dictionary<int, long> LagByPartition = [];
    private static readonly object Gate = new();

    static PersisterMetrics()
    {
        Meter.CreateObservableGauge("persist_lag_ms", () =>
        {
            lock (Gate)
            {
                return LagByPartition.Select(p => new Measurement<long>(p.Value, new KeyValuePair<string, object?>("partition", p.Key))).ToList();
            }
        }, "ms");
    }

    public static readonly Counter<long> Persisted = Meter.CreateCounter<long>("persisted_entries_total");
    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("dead_lettered_entries_total");

    public static void SetLag(int partition, long lagMs)
    {
        lock (Gate)
        {
            LagByPartition[partition] = lagMs;
        }
    }
}

/// <summary>What one round did, and how long the caller should wait before the next.</summary>
public sealed record PersistRound(int Read, int Written, int DeadLettered, TimeSpan WaitBefore);

/// <summary>
/// Moves bid stream entries from Redis to SQL for one partition (spec 03 design 6, R8.3 to R8.5).
/// An entry is acknowledged only after its SQL transaction committed, so a crash at any point
/// means the entry is read again; the SQL writer skips rows it already has.
/// </summary>
public sealed class StreamPersister
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ScriptRegistry _scripts;
    private readonly PersistSqlWriter _sql;
    private readonly PersisterOptions _options;
    private readonly ILogger<StreamPersister> _log;
    private readonly TimeProvider _clock;
    private readonly int _partition;
    private readonly string _stream;
    private readonly string _deadLetter;

    private readonly Dictionary<string, int> _attempts = [];
    private bool _readPendingFirst = true;
    private int _sqlFailuresInARow;

    public StreamPersister(IConnectionMultiplexer redis, ScriptRegistry scripts, PersistSqlWriter sql, PersisterOptions options,
        ILogger<StreamPersister> log, TimeProvider clock, int partition)
    {
        _redis = redis;
        _scripts = scripts;
        _sql = sql;
        _options = options;
        _log = log;
        _clock = clock;
        _partition = partition;
        _stream = $"{{p{partition}}}:stream";
        _deadLetter = $"{{p{partition}}}:dlq";
    }

    private IDatabase Db => _redis.GetDatabase();

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Creates the group (and the stream) if needed, then takes over entries stuck at dead consumers.</summary>
    public async Task StartAsync()
    {
        try
        {
            await Db.StreamCreateConsumerGroupAsync(_stream, PersisterOptions.Group, "$", createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
            // already there
        }

        var claimed = await Db.StreamAutoClaimIdsOnlyAsync(_stream, PersisterOptions.Group, _options.Consumer, _options.ClaimIdleMs, "0-0", 1000);
        if (claimed.ClaimedIds.Length > 0)
        {
            _log.LogInformation("Took over {Count} stuck entries on {Stream}", claimed.ClaimedIds.Length, _stream);
        }

        _readPendingFirst = true;
    }

    /// <summary>One read, write, acknowledge round.</summary>
    public async Task<PersistRound> RunOnceAsync(CancellationToken ct = default)
    {
        // Our own unacknowledged entries first (after a restart or a failed round), then new ones.
        var position = _readPendingFirst ? "0" : ">";
        var raw = await Db.StreamReadGroupAsync(_stream, PersisterOptions.Group, _options.Consumer, position, _options.BatchSize);
        if (raw.Length == 0 && _readPendingFirst)
        {
            _readPendingFirst = false;
            raw = await Db.StreamReadGroupAsync(_stream, PersisterOptions.Group, _options.Consumer, ">", _options.BatchSize);
        }

        if (raw.Length == 0)
        {
            return new PersistRound(0, 0, 0, TimeSpan.FromMilliseconds(50));   // nothing to do; SE.Redis has no blocking read
        }

        // Entries that cannot even be read are dead letters straight away: no retry can fix them.
        var good = new List<(StreamEntry Raw, PersistEntry Entry)>();
        var dead = 0;
        foreach (var entry in raw)
        {
            try
            {
                good.Add((entry, PersistEntryParser.Parse(entry.Id!, ToDictionary(entry))));
            }
            catch (FormatException ex)
            {
                await DeadLetterAsync(entry, ex.Message, attempts: 1, auctionId: TryAuction(entry));
                dead++;
            }
        }

        if (good.Count == 0)
        {
            return new PersistRound(raw.Length, 0, dead, TimeSpan.Zero);
        }

        try
        {
            await _sql.WriteAsync(PersistPlanner.Build(good.Select(g => g.Entry).ToList()), ct);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            return await HandleBatchFailureAsync(good, ex, raw.Length, dead, ct);
        }

        await AfterCommitAsync(good);
        _sqlFailuresInARow = 0;
        return new PersistRound(raw.Length, good.Count, dead, TimeSpan.Zero);
    }

    /// <summary>
    /// SQL down: wait and try the same entries again, without counting (nothing is wrong with them).
    /// SQL up: some entry is bad. Write them one at a time to find it; later entries of the same
    /// auction wait, so each auction stays in order.
    /// </summary>
    private async Task<PersistRound> HandleBatchFailureAsync(List<(StreamEntry Raw, PersistEntry Entry)> batch, Exception error,
        int read, int dead, CancellationToken ct)
    {
        _readPendingFirst = true;

        if (!await _sql.CanConnectAsync(ct))
        {
            _sqlFailuresInARow++;
            var wait = StreamMath.Backoff(_sqlFailuresInARow);
            _log.LogWarning(error, "SQL unavailable; {Count} entries stay unacknowledged, retrying in {Wait} ms", batch.Count, wait.TotalMilliseconds);
            return new PersistRound(read, 0, dead, wait);
        }

        _sqlFailuresInARow = 0;
        var written = 0;
        var blocked = new HashSet<Guid>();
        foreach (var item in batch)
        {
            if (blocked.Contains(item.Entry.AuctionId))
            {
                continue;
            }

            try
            {
                await _sql.WriteAsync(PersistPlanner.Build([item.Entry]), ct);
                await AfterCommitAsync([item]);
                written++;
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
            {
                blocked.Add(item.Entry.AuctionId);
                var attempts = _attempts.GetValueOrDefault(item.Entry.Id) + 1;
                _attempts[item.Entry.Id] = attempts;

                if (attempts >= _options.MaxAttempts)
                {
                    await DeadLetterAsync(item.Raw, ex.Message, attempts, item.Entry.AuctionId);
                    dead++;
                    blocked.Remove(item.Entry.AuctionId);   // the auction is frozen; its later entries may still be written
                }
                else
                {
                    _log.LogWarning(ex, "Entry {Id} for auction {AuctionId} failed (try {Attempt} of {Max})",
                        item.Entry.Id, item.Entry.AuctionId, attempts, _options.MaxAttempts);
                }
            }
        }

        return new PersistRound(read, written, dead, written == 0 ? StreamMath.Backoff(1) : TimeSpan.Zero);
    }

    /// <summary>After COMMIT: move persistedEseq forward per auction, then XACK (design 6).</summary>
    private async Task AfterCommitAsync(IReadOnlyList<(StreamEntry Raw, PersistEntry Entry)> written)
    {
        foreach (var auction in written.GroupBy(w => w.Entry.AuctionId))
        {
            var keys = new AuctionKeys(auction.Key, _partition);
            await _scripts.EvaluateAsync("persisted.lua", [keys.State], [auction.Max(w => w.Entry.ESeq)]);
        }

        var ids = written.Select(w => w.Raw.Id).ToArray();
        await Db.StreamAcknowledgeAsync(_stream, PersisterOptions.Group, ids);
        foreach (var w in written)
        {
            _attempts.Remove(w.Entry.Id);
        }

        PersisterMetrics.Persisted.Add(written.Count);
    }

    /// <summary>Copy to {pN}:dlq, acknowledge the original, freeze the auction, log Critical (R8.5).</summary>
    private async Task DeadLetterAsync(StreamEntry entry, string reason, int attempts, Guid? auctionId)
    {
        var fields = entry.Values.ToList();
        fields.Add(new NameValueEntry("dlqReason", reason.Length <= 500 ? reason : reason[..500]));
        fields.Add(new NameValueEntry("dlqAttempts", attempts));
        fields.Add(new NameValueEntry("dlqOriginalId", entry.Id));
        await Db.StreamAddAsync(_deadLetter, fields.ToArray());
        await Db.StreamAcknowledgeAsync(_stream, PersisterOptions.Group, entry.Id);
        _attempts.Remove(entry.Id!);

        if (auctionId != null)
        {
            var keys = new AuctionKeys(auctionId.Value, _partition);
            await _scripts.EvaluateAsync("freeze.lua", [keys.State, keys.Events, keys.Stream], [auctionId.Value.ToString(), "persist-dlq"]);
        }

        PersisterMetrics.DeadLettered.Add(1);
        _log.LogCritical("PersistDLQ: entry {Id} for auction {AuctionId} moved to {Dlq} after {Attempts} tries: {Reason}",
            entry.Id, auctionId, _deadLetter, attempts, reason);
    }

    /// <summary>XTRIM MINID to the lowest id any group still needs, keeping the last 10 minutes. Returns the MINID used.</summary>
    public async Task<string?> TrimAsync()
    {
        var groups = await Db.StreamGroupInfoAsync(_stream);
        var positions = new List<GroupPosition>();
        foreach (var group in groups)
        {
            string? lowestPending = null;
            if (group.PendingMessageCount > 0)
            {
                var pending = await Db.StreamPendingAsync(_stream, group.Name);
                lowestPending = pending.LowestPendingMessageId;
            }

            positions.Add(new GroupPosition(group.Name, lowestPending, group.LastDeliveredId));
        }

        var minId = StreamMath.TrimMinId(positions, NowMs);
        if (minId != null)
        {
            await Db.ExecuteAsync("XTRIM", _stream, "MINID", minId);
        }

        return minId;
    }

    /// <summary>Works out persist_lag_ms and publishes it on the gauge.</summary>
    public async Task<long> MeasureLagAsync()
    {
        string? lowestPending = null;
        string? lastDelivered = null;
        foreach (var group in await Db.StreamGroupInfoAsync(_stream))
        {
            if (group.Name != PersisterOptions.Group)
            {
                continue;
            }

            lastDelivered = group.LastDeliveredId;
            if (group.PendingMessageCount > 0)
            {
                lowestPending = (await Db.StreamPendingAsync(_stream, PersisterOptions.Group)).LowestPendingMessageId;
            }
        }

        string? firstUndelivered = null;
        if (lastDelivered != null)
        {
            var next = await Db.StreamRangeAsync(_stream, minId: "(" + lastDelivered, count: 1);
            firstUndelivered = next.Length > 0 ? next[0].Id.ToString() : null;
        }

        var lag = StreamMath.LagMs(NowMs, lowestPending, firstUndelivered);
        PersisterMetrics.SetLag(_partition, lag);
        return lag;
    }

    private static Dictionary<string, string> ToDictionary(StreamEntry entry)
    {
        var fields = new Dictionary<string, string>();
        foreach (var value in entry.Values)
        {
            fields[value.Name!] = value.Value.ToString();
        }

        return fields;
    }

    private static Guid? TryAuction(StreamEntry entry) =>
        Guid.TryParse(entry["auction"].ToString(), out var id) ? id : null;
}
