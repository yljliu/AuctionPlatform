using Auction.Domain.Bidding;
using Auction.Infrastructure.Redis;
using StackExchange.Redis;

namespace Auction.Integration.Tests.Fixtures;

/// <summary>
/// Writes a live auction's Redis state the way spec 05 will when it opens an auction,
/// so the bid script can be tested on its own.
/// </summary>
public static class LiveAuctionSeeder
{
    public const string Table1 = "0:100,5000:250,25000:500,100000:1000,500000:5000";

    public sealed record Setup(
        Guid AuctionId,
        Guid SellerId,
        string Status = "Live",
        long StartC = 1000,
        long PriceC = 0,
        Guid? Leader = null,
        long LeaderMaxC = 0,
        int BidCount = 0,
        long StartsInMs = -3_600_000,
        long EndsInMs = 3_600_000,
        long SnipeMs = 0,
        long ExtMs = 0,
        long MaxExtMs = 0,
        long ExtUsedMs = 0,
        string Inc = Table1,
        string Currency = "USD");

    /// <summary>partition: put the keys in {pN} with this N (tests use their own N so streams do not mix).</summary>
    public static async Task<AuctionKeys> SeedAsync(IDatabase db, Setup setup, int? partition = null)
    {
        var keys = partition == null ? RedisKeys.For(setup.AuctionId, 1) : new AuctionKeys(setup.AuctionId, partition.Value);
        await db.KeyDeleteAsync([keys.State, keys.Max, keys.Bids, keys.Events]);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.HashSetAsync(keys.State,
        [
            new("status", setup.Status),
            new("seller", setup.SellerId.ToString()),
            new("startMs", now + setup.StartsInMs),
            new("endMs", now + setup.EndsInMs),
            new("schedEndMs", now + setup.EndsInMs),
            new("startC", setup.StartC),
            new("reserveC", ""),
            new("priceC", setup.PriceC),
            new("leader", setup.Leader?.ToString() ?? ""),
            new("leaderMaxC", setup.LeaderMaxC),
            new("bidCount", setup.BidCount),
            new("seq", 0),
            new("eseq", 0),
            new("persistedEseq", 0),
            new("snipeMs", setup.SnipeMs),
            new("extMs", setup.ExtMs),
            new("maxExtMs", setup.MaxExtMs),
            new("extUsedMs", setup.ExtUsedMs),
            new("inc", setup.Inc),
            new("cur", setup.Currency),
            new("watchers", 0),
        ]);

        if (setup.Leader != null)
        {
            await db.HashSetAsync(keys.Max, setup.Leader.Value.ToString(), setup.LeaderMaxC);
        }

        return keys;
    }

    public static async Task<BidState> ReadBidStateAsync(IDatabase db, AuctionKeys keys)
    {
        var v = await db.HashGetAsync(keys.State, ["priceC", "leader", "leaderMaxC", "bidCount", "startC"]);
        var leader = v[1].IsNullOrEmpty ? (Guid?)null : Guid.Parse(v[1].ToString());
        return new BidState((long)v[0], leader, (long)v[2], (int)v[3], (long)v[4]);
    }

    /// <summary>Removes the auction's keys and its entries from the partition stream.</summary>
    public static async Task CleanUpAsync(IDatabase db, AuctionKeys keys) =>
        await db.KeyDeleteAsync([keys.State, keys.Max, keys.Bids, keys.Events]);
}
