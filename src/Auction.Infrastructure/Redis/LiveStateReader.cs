using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Auction.Infrastructure.Redis;

/// <summary>The live numbers of one auction from its Redis state hash (spec 03 design 2).</summary>
public sealed record LiveState(
    string Status,
    long PriceC,
    int BidCount,
    long EndMs,
    long StartC,
    long? ReserveC,
    Guid? Leader,
    long Seq,
    long ExtUsedMs,
    int Watchers);

/// <summary>
/// One public bid row in {pN}:a:{id}:bids (JSON member of the ZSET). The bid script in spec 03 must
/// write exactly these fields. It never holds a max amount.
/// </summary>
public sealed record PublicBidJson(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("user")] Guid User,
    [property: JsonPropertyName("amountC")] long AmountC,
    [property: JsonPropertyName("atMs")] long AtMs,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>
/// Reads live auction data from Redis. Errors are not caught here: the caller decides to fall back
/// to SQL and set prices_delayed.
/// </summary>
public sealed class LiveStateReader(IConnectionMultiplexer redis, RedisOptions? options = null)
{
    private readonly RedisOptions _options = options ?? RedisOptions.Default;

    private AuctionKeys Keys(Guid auctionId) => RedisKeys.For(auctionId, _options);

    public const int RecentBids = 50;

    private static readonly RedisValue[] StateFields =
    [
        "status", "priceC", "bidCount", "endMs", "startC", "reserveC", "leader", "seq", "extUsedMs", "watchers",
    ];

    /// <summary>
    /// State of many auctions in one round trip (pipelined HMGET). Auctions without a Redis hash are left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, LiveState>> ReadManyAsync(IReadOnlyCollection<Guid> auctionIds)
    {
        var result = new Dictionary<Guid, LiveState>();
        if (auctionIds.Count == 0)
        {
            return result;
        }

        var database = redis.GetDatabase();
        var batch = database.CreateBatch();
        var pending = new List<(Guid Id, Task<RedisValue[]> Task)>();
        foreach (var id in auctionIds)
        {
            pending.Add((id, batch.HashGetAsync(Keys(id).State, StateFields)));
        }

        batch.Execute();
        await Task.WhenAll(pending.Select(p => p.Task));

        foreach (var (id, task) in pending)
        {
            var state = Parse(task.Result);
            if (state != null)
            {
                result[id] = state;
            }
        }

        return result;
    }

    /// <summary>State plus the last 50 public bid rows, newest first. Null when Redis has no state for it.</summary>
    public async Task<(LiveState State, IReadOnlyList<PublicBidJson> Bids)?> ReadDetailAsync(Guid auctionId)
    {
        var database = redis.GetDatabase();
        var keys = Keys(auctionId);
        var stateTask = database.HashGetAsync(keys.State, StateFields);
        var bidsTask = database.SortedSetRangeByRankAsync(keys.Bids, 0, RecentBids - 1, Order.Descending);
        await Task.WhenAll(stateTask, bidsTask);

        var state = Parse(stateTask.Result);
        if (state == null)
        {
            return null;
        }

        var bids = new List<PublicBidJson>();
        foreach (var member in bidsTask.Result)
        {
            try
            {
                var row = JsonSerializer.Deserialize<PublicBidJson>(member.ToString());
                if (row != null)
                {
                    bids.Add(row);
                }
            }
            catch (JsonException)
            {
                // skip a bad row rather than fail the page
            }
        }

        return (state, bids);
    }

    /// <summary>The caller's own max in cents, or null. Only ever read for the signed-in caller.</summary>
    public async Task<long?> ReadMyMaxAsync(Guid auctionId, Guid userId)
    {
        var value = await redis.GetDatabase().HashGetAsync(Keys(auctionId).Max, userId.ToString());
        return value.HasValue && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cents) ? cents : null;
    }

    private static LiveState? Parse(RedisValue[] values)
    {
        if (values.Length == 0 || values[0].IsNullOrEmpty)
        {
            return null;   // no hash: the auction is not live in Redis
        }

        return new LiveState(
            Status: values[0].ToString(),
            PriceC: Long(values[1]),
            BidCount: (int)Long(values[2]),
            EndMs: Long(values[3]),
            StartC: Long(values[4]),
            ReserveC: values[5].IsNullOrEmpty ? null : Long(values[5]),
            Leader: Guid.TryParse(values[6].ToString(), out var leader) ? leader : null,
            Seq: Long(values[7]),
            ExtUsedMs: Long(values[8]),
            Watchers: (int)Long(values[9]));
    }

    private static long Long(RedisValue value) =>
        long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
}
