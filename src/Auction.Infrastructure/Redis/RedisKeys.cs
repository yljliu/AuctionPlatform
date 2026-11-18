namespace Auction.Infrastructure.Redis;

/// <summary>All Redis key names in one place.</summary>
public static class RedisKeys
{
    /// <summary>{u:ID}:status  (JSON: status, sv)</summary>
    public static string UserStatus(Guid userId) => $"{{u:{userId}}}:status";

    /// <summary>
    /// Partition hash tag for auction keys (spec 03 design 1). MVP has one partition, so always {p0}.
    /// All keys of one auction share it, so one Lua script can touch them together.
    /// </summary>
    public static string Partition(Guid auctionId) => "{p0}";

    /// <summary>{pN}:a:{id}:state  (hash, spec 03 design 2)</summary>
    public static string AuctionState(Guid auctionId) => $"{Partition(auctionId)}:a:{auctionId}:state";

    /// <summary>{pN}:a:{id}:bids  (ZSET of public bid row JSON, last 50)</summary>
    public static string AuctionBids(Guid auctionId) => $"{Partition(auctionId)}:a:{auctionId}:bids";

    /// <summary>{pN}:a:{id}:max  (hash userId to max cents; private)</summary>
    public static string AuctionMax(Guid auctionId) => $"{Partition(auctionId)}:a:{auctionId}:max";

    /// <summary>cache:cattree  (JSON of the active category tree, 10 minutes)</summary>
    public const string CategoryTree = "cache:cattree";

    /// <summary>rl:scope:id  (hash: tokens, ts)</summary>
    public static string RateLimit(string scope, string id) => $"rl:{scope}:{id}";
}
