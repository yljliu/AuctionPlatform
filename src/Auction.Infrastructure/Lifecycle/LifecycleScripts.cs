using System.Globalization;
using Auction.Infrastructure.Redis;
using StackExchange.Redis;

namespace Auction.Infrastructure.Lifecycle;

/// <summary>What a lifecycle script did.</summary>
public enum LifecycleResult
{
    /// <summary>The change happened (loaded, opened, sent, sealed, settled, rebuilt, ok).</summary>
    Done,

    /// <summary>Not time yet; the index entry was re-scored.</summary>
    NotDue,

    /// <summary>Nothing to do: already past this step (running twice is safe).</summary>
    Skipped,

    /// <summary>The state already exists (load, rebuild).</summary>
    Exists,

    /// <summary>The action is not allowed in this status (control ops; maps to INVALID_STATE).</summary>
    InvalidState,

    /// <summary>No Redis state for this auction.</summary>
    Missing,
}

/// <summary>Values for load.lua: a Scheduled auction from SQL.</summary>
public sealed record AuctionLoad(
    Guid SellerId,
    long StartMs,
    long EndMs,
    long StartC,
    long? ReserveC,
    long SnipeMs,
    long ExtMs,
    long MaxExtMs,
    string IncrementTable,
    string Currency,
    int Watchers = 0);

/// <summary>Values for rebuild.lua: a Live auction whose Redis state was lost.</summary>
public sealed record AuctionRebuild(
    AuctionLoad Auction,
    long SchedEndMs,
    long ExtUsedMs,
    long PriceC,
    Guid? LeaderId,
    long LeaderMaxC,
    int BidCount,
    long Seq,
    long ESeq,
    IReadOnlyDictionary<Guid, long> MaxByUser);

public enum ControlOp
{
    Cancel,
    Close,
    Freeze,
    Resume,
}

/// <summary>Runs the lifecycle Lua scripts (spec 05 design 5). Every script gets the same nine keys.</summary>
public sealed class LifecycleScripts(ScriptRegistry scripts)
{
    public Task<LifecycleResult> LoadAsync(AuctionKeys keys, AuctionLoad a) =>
        RunAsync("load.lua", keys, [.. LoadArgs(a)]);

    public Task<LifecycleResult> OpenAsync(AuctionKeys keys) => RunAsync("open.lua", keys);

    public Task<LifecycleResult> SoonAsync(AuctionKeys keys) => RunAsync("soon.lua", keys);

    public Task<LifecycleResult> SealAsync(AuctionKeys keys) => RunAsync("seal.lua", keys);

    /// <param name="finalC">Final price in cents; 0 when Unsold.</param>
    public Task<LifecycleResult> SettleAsync(AuctionKeys keys, string outcome, long finalC, Guid? winnerId) =>
        RunAsync("settle.lua", keys, outcome, Num(finalC), winnerId?.ToString() ?? "");

    /// <param name="force">Admin cancel: cancel even when bids exist.</param>
    public Task<LifecycleResult> ControlAsync(AuctionKeys keys, ControlOp op, bool force = false) =>
        RunAsync("control.lua", keys, op.ToString().ToLowerInvariant(), force ? "1" : "0");

    public Task<LifecycleResult> RebuildAsync(AuctionKeys keys, AuctionRebuild r)
    {
        var a = r.Auction;
        var args = new List<RedisValue>
        {
            a.SellerId.ToString(), Num(a.StartMs), Num(a.EndMs), Num(r.SchedEndMs), Num(a.StartC),
            a.ReserveC == null ? "" : Num(a.ReserveC.Value), Num(a.SnipeMs), Num(a.ExtMs), Num(a.MaxExtMs),
            Num(r.ExtUsedMs), a.IncrementTable, a.Currency, Num(r.PriceC), r.LeaderId?.ToString() ?? "",
            Num(r.LeaderMaxC), Num(r.BidCount), Num(r.Seq), Num(r.ESeq),
        };
        foreach (var (user, maxC) in r.MaxByUser)
        {
            args.Add(user.ToString());
            args.Add(Num(maxC));
        }

        return RunAsync("rebuild.lua", keys, [.. args]);
    }

    /// <summary>The nine keys in the order state_emit.lua expects.</summary>
    public static RedisKey[] KeysFor(AuctionKeys keys)
    {
        var index = keys.Index;
        return [keys.State, keys.Events, keys.Stream, index.Starts, index.Ends, index.Soon, index.Sealed, keys.Max, keys.Bids];
    }

    public static LifecycleResult Parse(string? answer) => answer switch
    {
        "loaded" or "opened" or "sent" or "sealed" or "settled" or "rebuilt" or "ok" => LifecycleResult.Done,
        "not_due" => LifecycleResult.NotDue,
        "skip" => LifecycleResult.Skipped,
        "exists" => LifecycleResult.Exists,
        "invalid_state" => LifecycleResult.InvalidState,
        "missing" => LifecycleResult.Missing,
        _ => throw new InvalidOperationException($"Unknown lifecycle script answer '{answer}'."),
    };

    private async Task<LifecycleResult> RunAsync(string script, AuctionKeys keys, params RedisValue[] extra)
    {
        RedisValue[] args = [keys.AuctionId.ToString(), .. extra];
        var result = await scripts.EvaluateAsync(script, KeysFor(keys), args);
        return Parse((string?)result);
    }

    private static IEnumerable<RedisValue> LoadArgs(AuctionLoad a) =>
    [
        a.SellerId.ToString(), Num(a.StartMs), Num(a.EndMs), Num(a.StartC), a.ReserveC == null ? "" : Num(a.ReserveC.Value),
        Num(a.SnipeMs), Num(a.ExtMs), Num(a.MaxExtMs), a.IncrementTable, a.Currency, Num(a.Watchers),
    ];

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
}
