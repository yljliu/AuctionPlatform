using Auction.Domain.Bidding;

namespace Auction.Infrastructure.Redis;

/// <summary>Bound from config section "Redis" (env Redis__Partitions).</summary>
public sealed class RedisOptions
{
    /// <summary>
    /// How many partitions auctions are spread over. MVP uses 1. Never change it while auctions are
    /// live, because each auction's keys (and its stream) would move to another partition.
    /// </summary>
    public int Partitions { get; set; } = 1;

    /// <summary>
    /// When true, a bid is confirmed only after one replica has it (WAIT 1 50, R8.1).
    /// Turn on only when a replica runs (full compose); with no replica every bid would fail.
    /// </summary>
    public bool RequireReplicaAck { get; set; }

    public static readonly RedisOptions Default = new();

    /// <summary>Throws when Partitions is out of range, so a bad config stops the app at start.</summary>
    public RedisOptions Validate()
    {
        if (Partitions < 1 || Partitions > AuctionPartitioner.MaxPartitions)
        {
            throw new InvalidOperationException($"Redis:Partitions must be 1 to {AuctionPartitioner.MaxPartitions}, not {Partitions}.");
        }

        return this;
    }
}

/// <summary>
/// Every Redis key of one auction (spec 03 design 1). All start with the same {pN} hash tag, so in a
/// Redis Cluster they live in one slot and one Lua script may touch them all, the stream included.
/// </summary>
public sealed record AuctionKeys(Guid AuctionId, int Partition)
{
    /// <summary>The hash tag, for example "{p0}". Redis hashes only the text inside the braces.</summary>
    public string Tag => $"{{p{Partition}}}";

    private string Prefix => $"{Tag}:a:{AuctionId}";

    /// <summary>Hash: status, prices, leader, seq... (design 2).</summary>
    public string State => Prefix + ":state";

    /// <summary>Hash: userId to max cents. Private.</summary>
    public string Max => Prefix + ":max";

    /// <summary>ZSET: last 50 public bid rows.</summary>
    public string Bids => Prefix + ":bids";

    /// <summary>ZSET: last 200 public events, for catch-up.</summary>
    public string Events => Prefix + ":events";

    /// <summary>String: stored outcome for one idempotency key, 24 hours.</summary>
    public string Idempotency(string key) => $"{Prefix}:idem:{key}";

    /// <summary>Stream for the whole partition (shared by every auction in it).</summary>
    public string Stream => Tag + ":stream";

    /// <summary>Dead-letter stream for the whole partition.</summary>
    public string DeadLetter => Tag + ":dlq";
}

/// <summary>All Redis key names in one place.</summary>
public static class RedisKeys
{
    /// <summary>All keys of one auction, in its partition.</summary>
    public static AuctionKeys For(Guid auctionId, int partitions) =>
        new(auctionId, AuctionPartitioner.PartitionOf(auctionId, partitions));

    /// <summary>All keys of one auction, using the configured partition count.</summary>
    public static AuctionKeys For(Guid auctionId, RedisOptions options) => For(auctionId, options.Partitions);

    /// <summary>{u:ID}:status  (JSON: status, sv)</summary>
    public static string UserStatus(Guid userId) => $"{{u:{userId}}}:status";

    /// <summary>h:{userId}  (username, 1 hour; spec 04 handle cache)</summary>
    public static string Handle(Guid userId) => $"h:{userId}";

    /// <summary>{pN}:a:{id}:state</summary>
    public static string AuctionState(Guid auctionId, int partitions = 1) => For(auctionId, partitions).State;

    /// <summary>{pN}:a:{id}:bids</summary>
    public static string AuctionBids(Guid auctionId, int partitions = 1) => For(auctionId, partitions).Bids;

    /// <summary>{pN}:a:{id}:max</summary>
    public static string AuctionMax(Guid auctionId, int partitions = 1) => For(auctionId, partitions).Max;

    /// <summary>cache:cattree  (JSON of the active category tree, 10 minutes)</summary>
    public const string CategoryTree = "cache:cattree";

    /// <summary>rl:scope:id  (hash: tokens, ts)</summary>
    public static string RateLimit(string scope, string id) => $"rl:{scope}:{id}";
}
