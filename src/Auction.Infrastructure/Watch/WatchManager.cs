using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Watch;

public sealed record WatchResult(bool Watching, int Watchers);

/// <summary>
/// Watch and unwatch (spec 07 task 1). Both are safe to repeat: the SQL row is added or removed only once,
/// and the Redis count moves only when SQL changed (design 4).
/// </summary>
public sealed class WatchManager(
    AuctionDbContext db,
    ScriptRegistry scripts,
    RedisOptions redisOptions,
    AuctionBrowser browser,
    TimeProvider clock,
    ILogger<WatchManager> log)
{
    private const string Add = """
        INSERT bidding.Watchlist (UserId, AuctionId, CreatedAt)
        SELECT {0}, {1}, {2}
        WHERE NOT EXISTS (SELECT 1 FROM bidding.Watchlist WITH (UPDLOCK, HOLDLOCK) WHERE UserId = {0} AND AuctionId = {1});
        """;

    public async Task<WatchResult> WatchAsync(Guid userId, Guid auctionId, CancellationToken ct = default)
    {
        var status = await db.Auctions.AsNoTracking().Where(a => a.AuctionId == auctionId).Select(a => (AuctionStatus?)a.Status).FirstOrDefaultAsync(ct)
                     ?? throw new DomainException(ErrorCodes.NotFound, "Auction not found.");
        if (status is AuctionStatus.Draft)
        {
            throw new DomainException(ErrorCodes.NotFound, "Auction not found.");
        }

        if (status is AuctionStatus.Settled or AuctionStatus.Cancelled)
        {
            throw new DomainException(ErrorCodes.InvalidState, "This auction has ended, so it can't be watched.");
        }

        var added = await db.Database.ExecuteSqlRawAsync(Add, [userId, auctionId, clock.GetUtcNow().UtcDateTime], ct);
        var count = added == 1 ? await MoveRedisCountAsync(auctionId, +1) : -1;
        return new WatchResult(true, count >= 0 ? count : await CountAsync(auctionId, ct));
    }

    public async Task<WatchResult> UnwatchAsync(Guid userId, Guid auctionId, CancellationToken ct = default)
    {
        var removed = await db.Watchlist.Where(w => w.UserId == userId && w.AuctionId == auctionId).ExecuteDeleteAsync(ct);
        var count = removed == 1 ? await MoveRedisCountAsync(auctionId, -1) : -1;
        return new WatchResult(false, count >= 0 ? count : await CountAsync(auctionId, ct));
    }

    /// <summary>Live and Scheduled auctions the user watches (R1.5), ending soonest first, with live prices.</summary>
    public async Task<BrowseResult> ListWatchedAsync(Guid userId, Paging paging, CancellationToken ct = default)
    {
        var ids = await db.Watchlist.AsNoTracking()
            .Where(w => w.UserId == userId)
            .Join(db.Auctions.Where(a => a.Status == AuctionStatus.Live || a.Status == AuctionStatus.Scheduled),
                w => w.AuctionId, a => a.AuctionId, (w, a) => new { a.AuctionId, a.EndTime })
            .OrderBy(x => x.EndTime).ThenBy(x => x.AuctionId)
            .Select(x => x.AuctionId)
            .ToListAsync(ct);

        return await browser.BrowseIdsAsync(ids, paging, userId, ct);
    }

    private Task<int> CountAsync(Guid auctionId, CancellationToken ct) => db.Watchlist.CountAsync(w => w.AuctionId == auctionId, ct);

    // Redis is only a fast copy; if it is down the SQL row still counts and load.lua fixes the number later.
    private async Task<int> MoveRedisCountAsync(Guid auctionId, int delta)
    {
        try
        {
            var keys = RedisKeys.For(auctionId, redisOptions);
            return (int)(long)await scripts.EvaluateAsync("watchers.lua", [keys.State], [delta]);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogWarning(ex, "Could not update the Redis watcher count for {AuctionId}", auctionId);
            return -1;
        }
    }
}
