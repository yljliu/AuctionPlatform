namespace Auction.Infrastructure.Redis;

/// <summary>All Redis key names in one place.</summary>
public static class RedisKeys
{
    /// <summary>{u:ID}:status  (JSON: status, sv)</summary>
    public static string UserStatus(Guid userId) => $"{{u:{userId}}}:status";

    /// <summary>cache:cattree  (JSON of the active category tree, 10 minutes)</summary>
    public const string CategoryTree = "cache:cattree";

    /// <summary>rl:scope:id  (hash: tokens, ts)</summary>
    public static string RateLimit(string scope, string id) => $"rl:{scope}:{id}";
}
