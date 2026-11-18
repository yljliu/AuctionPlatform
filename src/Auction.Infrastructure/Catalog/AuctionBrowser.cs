using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Catalog;

public sealed record BrowseResult(
    IReadOnlyList<AuctionCardRow> Rows,
    int Total,
    bool PricesDelayed,
    IReadOnlySet<Guid> Watching);

/// <summary>
/// Browse and search with live numbers (R6.3 to R6.5):
/// 1. SQL finds the page of auctions.
/// 2. For Live ones, price, bid count, end time and status are replaced with Redis values in one round trip.
/// 3. If Redis fails, the SQL values stay and PricesDelayed is true.
/// 4. For a signed-in caller, Watching lists the auctions on this page that they watch.
/// </summary>
public sealed class AuctionBrowser(AuctionQueries queries, LiveStateReader live, AuctionDbContext db, ILogger<AuctionBrowser> log)
{
    public async Task<BrowseResult> BrowseAsync(AuctionSearch search, Paging paging, Guid? viewerId, CancellationToken ct = default)
    {
        var page = await queries.SearchAsync(search, paging, ct);

        var liveIds = page.Rows.Where(r => r.Status == AuctionStatus.Live).Select(r => r.AuctionId).ToList();
        var delayed = false;
        if (liveIds.Count > 0)
        {
            try
            {
                var states = await live.ReadManyAsync(liveIds);
                foreach (var row in page.Rows)
                {
                    if (states.TryGetValue(row.AuctionId, out var state))
                    {
                        ApplyLive(row, state);
                    }
                }
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                log.LogWarning(ex, "Redis unavailable for browse; showing SQL prices");
                delayed = true;
            }
        }

        var watching = await WatchingAsync(viewerId, page.Rows.Select(r => r.AuctionId).ToList(), ct);
        return new BrowseResult(page.Rows, page.Total, delayed, watching);
    }

    /// <summary>Redis values win for a live auction, so browse shows the same price as the auction page.</summary>
    public static void ApplyLive(AuctionCardRow row, LiveState state)
    {
        row.BidCount = state.BidCount;
        if (state.BidCount > 0)
        {
            row.Price = state.PriceC / 100m;
        }

        if (state.EndMs > 0)
        {
            row.EndTime = DateTimeOffset.FromUnixTimeMilliseconds(state.EndMs).UtcDateTime;
        }

        if (EnumText.TryParse<AuctionStatus>(state.Status, out var status))
        {
            row.Status = status;
        }
    }

    private async Task<IReadOnlySet<Guid>> WatchingAsync(Guid? viewerId, List<Guid> auctionIds, CancellationToken ct)
    {
        if (viewerId == null || auctionIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = await db.Watchlist.AsNoTracking()
            .Where(w => w.UserId == viewerId && auctionIds.Contains(w.AuctionId))
            .Select(w => w.AuctionId)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }
}
