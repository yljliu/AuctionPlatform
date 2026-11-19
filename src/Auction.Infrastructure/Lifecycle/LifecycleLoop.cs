using Auction.Domain.Enums;
using Auction.Infrastructure.Redis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Lifecycle;

/// <summary>Lifecycle loop settings (spec 05 design 4).</summary>
public sealed class LifecycleOptions
{
    public TimeSpan TickEvery { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often step A asks SQL for auctions about to start. Opening still happens on every tick.</summary>
    public TimeSpan LoadEvery { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Auctions starting within this time are put into Redis (R2.1).</summary>
    public TimeSpan LoadAhead { get; set; } = TimeSpan.FromMinutes(5);

    public int BatchSize { get; set; } = 100;

    /// <summary>Tests only: run these partitions instead of 0..Partitions-1.</summary>
    public IReadOnlyList<int>? OnlyPartitions { get; set; }

    /// <summary>Tests only: where an auction's keys live. Default: RedisKeys.For with the configured partitions.</summary>
    public Func<Guid, AuctionKeys>? KeysFor { get; set; }
}

/// <summary>What one tick did, for logs and tests.</summary>
public sealed record TickReport(int Loaded, int Opened, int SoonSent, int Sealed, int Settled = 0);

/// <summary>What recovery on leader start did (spec 05 design 7).</summary>
public sealed record RecoveryReport(int Indexed, int Rebuilt, int SettledFromSql);

/// <summary>
/// The lifecycle tick (spec 05 design 4): A load, B open, C closing-soon, D seal, E settle.
/// Only the lease holder calls TickAsync, and RecoverAsync once when it becomes leader.
/// Redis decides the time (TIME, once per tick).
/// Every SQL write is fenced with the grant, so a stale leader cannot change SQL (R1.3).
/// </summary>
public sealed class LifecycleLoop(
    IConnectionMultiplexer redis,
    LifecycleScripts scripts,
    LifecycleSql sql,
    Settler settler,
    RedisOptions redisOptions,
    LifecycleOptions options,
    ILogger<LifecycleLoop> log)
{
    private readonly HashSet<Guid> _loaded = [];
    private readonly Dictionary<int, string> _tables = [];
    private long? _lastLoadMs;
    private readonly Dictionary<Guid, long> _lagWarnedAtMs = [];
    private readonly HashSet<Guid> _cannotSettle = [];

    /// <summary>Sealed auctions wait this long for their bids to reach SQL before a warning (R5.2).</summary>
    public static readonly TimeSpan DrainWarnAfter = TimeSpan.FromSeconds(30);

    private IEnumerable<int> Partitions => options.OnlyPartitions ?? Enumerable.Range(0, redisOptions.Partitions);

    private AuctionKeys KeysFor(Guid auctionId) => options.KeysFor?.Invoke(auctionId) ?? RedisKeys.For(auctionId, redisOptions);

    /// <summary>Step A runs on the first tick, then once per LoadEvery. (Never subtract from long.MinValue: it wraps.)</summary>
    public static bool LoadDue(long nowMs, long? lastLoadMs, TimeSpan every) =>
        lastLoadMs == null || nowMs - lastLoadMs.Value >= (long)every.TotalMilliseconds;

    /// <summary>Forget what this copy loaded, so it starts clean after losing and winning the lease again.</summary>
    public void Reset()
    {
        _loaded.Clear();
        _lastLoadMs = null;
        _lagWarnedAtMs.Clear();
        _cannotSettle.Clear();
    }

    public async Task<TickReport> TickAsync(LeaseGrant grant, CancellationToken ct = default)
    {
        var nowMs = await RedisNowMsAsync();

        var loaded = 0;
        if (LoadDue(nowMs, _lastLoadMs, options.LoadEvery))
        {
            loaded = await LoadStepAsync(grant, nowMs, ct);
            _lastLoadMs = nowMs;
        }

        int opened = 0, soon = 0, sealedCount = 0, settled = 0;
        double maxLagMs = 0;
        foreach (var partition in Partitions)
        {
            var index = new PartitionKeys(partition);
            opened += await OpenStepAsync(grant, index, nowMs, ct);
            soon += await SoonStepAsync(index, nowMs);
            sealedCount += await SealStepAsync(grant, index, nowMs, ct);
            var (done, lagMs) = await SettleStepAsync(grant, index, nowMs, ct);
            settled += done;
            maxLagMs = Math.Max(maxLagMs, lagMs);
        }

        LifecycleMetrics.SetSettlementLag(maxLagMs / 1000.0);
        return new TickReport(loaded, opened, soon, sealedCount, settled);
    }

    // A. Scheduled auctions starting within 5 minutes go into Redis (R2.1).
    private async Task<int> LoadStepAsync(LeaseGrant grant, long nowMs, CancellationToken ct)
    {
        var until = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime + options.LoadAhead;
        var rows = await sql.ScheduledStartingByAsync(until, options.BatchSize * 5, ct);
        var count = 0;

        foreach (var row in rows)
        {
            if (_loaded.Contains(row.AuctionId))
            {
                continue;
            }

            try
            {
                var table = await TableAsync(row.IncrementTableId, ct);
                if (table == null)
                {
                    log.LogError("Auction {AuctionId} uses increment table {TableId}, which has no valid tiers; not loaded",
                        row.AuctionId, row.IncrementTableId);
                    continue;
                }

                var keys = KeysFor(row.AuctionId);
                var result = await scripts.LoadAsync(keys, row.ToLoad(table));
                if (result == LifecycleResult.Done)
                {
                    count++;
                }
                else
                {
                    // Already in Redis. If Redis moved on (opened, but the SQL update was lost), bring SQL up to date.
                    await RepairSqlStatusAsync(grant, keys, ct);
                }

                _loaded.Add(row.AuctionId);
            }
            catch (StaleFencingTokenException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not load auction {AuctionId}; trying again next time", row.AuctionId);
            }
        }

        return count;
    }

    // B. Scheduled -> Live at the start time, in Redis then SQL (R2.2).
    private async Task<int> OpenStepAsync(LeaseGrant grant, PartitionKeys index, long nowMs, CancellationToken ct)
    {
        var count = 0;
        foreach (var id in await DueAsync(index.Starts, nowMs))
        {
            await GuardAsync(id, "open", async () =>
            {
                if (await scripts.OpenAsync(new AuctionKeys(id, index.Partition)) == LifecycleResult.Done)
                {
                    count++;
                    _loaded.Remove(id);
                    await sql.SetStatusAsync(grant, id, AuctionStatus.Scheduled, AuctionStatus.Live, ct);
                }
            });
        }

        return count;
    }

    // C. One ClosingSoon event when 60 s or less are left (R3.1). Redis only.
    private async Task<int> SoonStepAsync(PartitionKeys index, long nowMs)
    {
        var count = 0;
        foreach (var id in await DueAsync(index.Soon, nowMs))
        {
            await GuardAsync(id, "closing-soon", async () =>
            {
                if (await scripts.SoonAsync(new AuctionKeys(id, index.Partition)) == LifecycleResult.Done)
                {
                    count++;
                }
            });
        }

        return count;
    }

    // D. Live -> Sealed at the current end time, in Redis then SQL (R4.1, R4.2).
    private async Task<int> SealStepAsync(LeaseGrant grant, PartitionKeys index, long nowMs, CancellationToken ct)
    {
        var count = 0;
        foreach (var id in await DueAsync(index.Ends, nowMs))
        {
            await GuardAsync(id, "seal", async () =>
            {
                if (await scripts.SealAsync(new AuctionKeys(id, index.Partition)) == LifecycleResult.Done)
                {
                    count++;
                    await sql.SetStatusAsync(grant, id, AuctionStatus.Live, AuctionStatus.Sealed, ct);
                }
            });
        }

        return count;
    }

    // E. Sealed auctions settle once the persister has written every stream entry for them (R5.1, R5.2).
    private async Task<(int Settled, double MaxLagMs)> SettleStepAsync(LeaseGrant grant, PartitionKeys index, long nowMs, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var settled = 0;
        double maxLagMs = 0;
        var ids = (await db.SortedSetRangeByRankAsync(index.Sealed, 0, options.BatchSize - 1))
            .Select(m => Guid.TryParse(m.ToString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty);

        foreach (var id in ids)
        {
            if (_cannotSettle.Contains(id))
            {
                continue;
            }

            var keys = new AuctionKeys(id, index.Partition);
            await GuardAsync(id, "settle", async () =>
            {
                var v = await db.HashGetAsync(keys.State, ["status", "eseq", "persistedEseq", "sealedAtMs"]);
                if (v[0].IsNull)
                {
                    // Redis lost it; recovery settles it from SQL on the next leader start.
                    await db.SortedSetRemoveAsync(index.Sealed, id.ToString());
                    return;
                }

                var eseq = (long)v[1];
                var persisted = v[2].IsNull ? 0 : (long)v[2];
                if (persisted < eseq)
                {
                    var lagMs = nowMs - (v[3].IsNull ? nowMs : (long)v[3]);
                    maxLagMs = Math.Max(maxLagMs, lagMs);
                    WarnIfLagging(id, lagMs, nowMs, eseq - persisted);
                    return;
                }

                var result = await settler.SettleAsync(grant, id, ct);
                if (result == null)
                {
                    _cannotSettle.Add(id);   // logged by the settler; do not retry every tick
                    return;
                }

                await scripts.SettleAsync(keys, result.Outcome.ToString(), result.FinalC, result.WinnerId);
                _lagWarnedAtMs.Remove(id);
                settled++;
                LifecycleMetrics.Settled.Add(1);
            });
        }

        return (settled, maxLagMs);
    }

    private void WarnIfLagging(Guid id, long lagMs, long nowMs, long behind)
    {
        var warnAfter = (long)DrainWarnAfter.TotalMilliseconds;
        if (lagMs <= warnAfter)
        {
            return;
        }

        // Once per 30 s per auction. Bidding stays closed; it only waits (R5.2).
        if (_lagWarnedAtMs.TryGetValue(id, out var last) && nowMs - last < warnAfter)
        {
            return;
        }

        _lagWarnedAtMs[id] = nowMs;
        log.LogWarning("Auction {AuctionId} sealed {LagSeconds:F0} s ago but {Behind} stream entries are not in SQL yet; settlement waits",
            id, lagMs / 1000.0, behind);
    }

    /// <summary>
    /// Run once when this copy becomes leader (spec 05 design 7, R7): rebuild the index sets from SQL,
    /// rebuild lost Live auctions as Frozen, and settle Sealed auctions whose Redis state is gone.
    /// Scheduled auctions are loaded by the normal step A.
    /// </summary>
    public async Task<RecoveryReport> RecoverAsync(LeaseGrant grant, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        int indexed = 0, rebuilt = 0, settledFromSql = 0;

        foreach (var row in await sql.OpenAuctionsAsync(ct))
        {
            var keys = KeysFor(row.AuctionId);
            var index = keys.Index;
            var v = await db.HashGetAsync(keys.State, ["status", "endMs", "soonSent", "sealedAtMs"]);
            var redisStatus = (string?)v[0];

            try
            {
                if (redisStatus == null)
                {
                    if (row.AuctionStatus == AuctionStatus.Sealed)
                    {
                        // Bidding is over and Redis is gone: SQL has everything the persister wrote.
                        if (await settler.SettleAsync(grant, row.AuctionId, ct) != null)
                        {
                            settledFromSql++;
                        }

                        continue;
                    }

                    if (await RebuildAsync(grant, keys, row, ct))
                    {
                        rebuilt++;
                    }

                    continue;
                }

                switch (redisStatus)
                {
                    case "Live":
                        await db.SortedSetAddAsync(index.Ends, row.AuctionId.ToString(), (long)v[1]);
                        if (v[2].IsNull)
                        {
                            await db.SortedSetAddAsync(index.Soon, row.AuctionId.ToString(), (long)v[1] - 60_000);
                        }

                        indexed++;
                        break;
                    case "Sealed":
                        await db.SortedSetAddAsync(index.Sealed, row.AuctionId.ToString(), v[3].IsNull ? 0 : (long)v[3]);
                        indexed++;
                        break;
                }
            }
            catch (StaleFencingTokenException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Recovery failed for auction {AuctionId}; it is tried again on the next leader start", row.AuctionId);
            }
        }

        log.LogInformation("Lifecycle recovery: {Indexed} indexed, {Rebuilt} rebuilt as Frozen, {Settled} settled from SQL",
            indexed, rebuilt, settledFromSql);
        return new RecoveryReport(indexed, rebuilt, settledFromSql);
    }

    // SQL says Live (or Frozen) but Redis lost the state: rebuild it Frozen; an admin must resume it (R7.2).
    private async Task<bool> RebuildAsync(LeaseGrant grant, AuctionKeys keys, OpenAuctionRow row, CancellationToken ct)
    {
        var table = await TableAsync(row.IncrementTableId, ct);
        if (table == null)
        {
            log.LogError("Auction {AuctionId} cannot be rebuilt: increment table {TableId} has no valid tiers", row.AuctionId, row.IncrementTableId);
            return false;
        }

        var maxByUser = await sql.MaxByUserAsync(row.AuctionId, ct);
        if (await scripts.RebuildAsync(keys, row.ToRebuild(table, maxByUser)) != LifecycleResult.Done)
        {
            return false;
        }

        await redis.GetDatabase().HashSetAsync(keys.State, "watchers", row.Watchers);   // spec 07

        if (row.AuctionStatus == AuctionStatus.Live)
        {
            await sql.SetStatusAsync(grant, row.AuctionId, AuctionStatus.Live, AuctionStatus.Frozen, ct);
        }

        log.LogCritical("AuctionRebuiltFrozen: auction {AuctionId} was {Status} in SQL but missing in Redis. " +
                        "It was rebuilt from SQL (price {Price}, leader {Leader}, {Bids} bids) and frozen; an admin must check and resume it",
            row.AuctionId, row.AuctionStatus, row.CurrentPrice, row.CurrentLeaderId, row.BidCount);
        return true;
    }

    private async Task RepairSqlStatusAsync(LeaseGrant grant, AuctionKeys keys, CancellationToken ct)
    {
        var status = (string?)await redis.GetDatabase().HashGetAsync(keys.State, "status");
        if (status is "Live" or "Sealed" or "Frozen" && Enum.TryParse<AuctionStatus>(status, out var redisStatus))
        {
            if (await sql.SetStatusAsync(grant, keys.AuctionId, AuctionStatus.Scheduled, redisStatus, ct))
            {
                log.LogWarning("Auction {AuctionId} was {Status} in Redis but Scheduled in SQL; SQL updated", keys.AuctionId, status);
            }
        }
    }

    // One auction failing must not stop the others. A stale token stops the whole tick.
    private async Task GuardAsync(Guid auctionId, string step, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (StaleFencingTokenException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Lifecycle step {Step} failed for auction {AuctionId}; trying again next tick", step, auctionId);
        }
    }

    private async Task<Guid[]> DueAsync(string indexKey, long nowMs)
    {
        var members = await redis.GetDatabase().SortedSetRangeByScoreAsync(
            indexKey, double.NegativeInfinity, nowMs, take: options.BatchSize);
        return members.Select(m => Guid.TryParse(m.ToString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToArray();
    }

    private async Task<string?> TableAsync(int tableId, CancellationToken ct)
    {
        if (_tables.TryGetValue(tableId, out var cached))
        {
            return cached;
        }

        var table = await sql.IncrementTableAsync(tableId, ct);
        if (table != null)
        {
            _tables[tableId] = table;
        }

        return table;
    }

    private async Task<long> RedisNowMsAsync()
    {
        var time = (RedisResult[])(await redis.GetDatabase().ExecuteAsync("TIME"))!;
        return ((long)time[0] * 1000) + ((long)time[1] / 1000);
    }
}
