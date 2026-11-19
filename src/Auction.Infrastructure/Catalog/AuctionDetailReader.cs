using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Domain.Identity;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Catalog;

/// <summary>One public bid row, with the bidder's handle already masked (or not) for this viewer.</summary>
public sealed record BidRowView(long Seq, Guid BidderId, string Handle, decimal Amount, long AtMs, bool Auto);

/// <summary>The caller's own position. Only built for the signed-in caller who has bid.</summary>
public sealed record MyBidView(decimal? MyMax, bool IsLeader, decimal MinNext);

/// <summary>Everything the auction page needs, already filtered for the viewer.</summary>
public sealed record AuctionDetailData(
    AuctionView View,
    AuctionStatus Status,
    decimal Price,
    int BidCount,
    DateTime EndTime,
    long Seq,
    int ExtensionUsedSec,
    int Watchers,
    decimal MinNext,
    Guid? LeaderId,
    string? LeaderHandle,
    IReadOnlyList<BidRowView> RecentBids,
    MyBidView? MyStatus,
    bool PricesDelayed,
    bool IsSellerView,
    bool Watching = false)
{
    /// <summary>Null when there is no reserve. Only "met or not" ever leaves the server.</summary>
    public bool? ReserveMet => View.Auction.ReservePrice == null ? null : BidCount > 0 && Price >= View.Auction.ReservePrice.Value;
}

/// <summary>
/// Builds the auction page (R7, design 6):
/// SQL for the auction, item, images and seller; Redis for live state and the last 50 bids while
/// Live or Sealed; SQL bids otherwise or if Redis is down. Handles are masked unless the viewer is
/// that bidder or an admin. The seller view (owner or admin) is marked with IsSellerView.
/// </summary>
public sealed class AuctionDetailReader(AuctionManager auctions, LiveStateReader live, AuctionDbContext db, ILogger<AuctionDetailReader> log)
{
    private static readonly AuctionStatus[] HiddenFromPublic = [AuctionStatus.Draft, AuctionStatus.Cancelled];

    public async Task<AuctionDetailData> GetAsync(Guid auctionId, Guid? viewerId, bool viewerIsAdmin, CancellationToken ct = default)
    {
        var view = await auctions.LoadViewAsync(auctionId, ct)
                   ?? throw new DomainException(ErrorCodes.NotFound, "Auction not found.");

        var auction = view.Auction;
        var isSellerView = viewerIsAdmin || viewerId == auction.SellerId;
        if (!isSellerView && HiddenFromPublic.Contains(auction.Status))
        {
            throw new DomainException(ErrorCodes.NotFound, "Auction not found.");
        }

        // Start with SQL values.
        var status = auction.Status;
        var price = auction.CurrentPrice ?? auction.StartingPrice;
        var bidCount = auction.BidCount;
        var endTime = auction.EndTime;
        var seq = auction.LastSeq;
        var extUsedSec = (int)Math.Max(0, (auction.EndTime - auction.ScheduledEndTime).TotalSeconds);
        var leaderId = auction.CurrentLeaderId;
        var watchers = -1;
        List<(long Seq, Guid User, decimal Amount, long AtMs, bool Auto)>? bids = null;
        var delayed = false;

        // Live values from Redis while it holds the auction.
        if (status is AuctionStatus.Live or AuctionStatus.Sealed)
        {
            try
            {
                var detail = await live.ReadDetailAsync(auctionId);
                if (detail != null)
                {
                    var (state, rows) = detail.Value;
                    if (Domain.Catalog.EnumText.TryParse<AuctionStatus>(state.Status, out var liveStatus))
                    {
                        status = liveStatus;
                    }

                    bidCount = state.BidCount;
                    price = state.BidCount > 0 ? state.PriceC / 100m : auction.StartingPrice;
                    endTime = state.EndMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(state.EndMs).UtcDateTime : endTime;
                    seq = state.Seq;
                    extUsedSec = (int)(state.ExtUsedMs / 1000);
                    leaderId = state.Leader;
                    watchers = state.Watchers;
                    bids = rows.Select(r => (r.Seq, r.User, r.AmountC / 100m, r.AtMs, IsAuto(r.Kind))).ToList();
                }
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                log.LogWarning(ex, "Redis unavailable for auction {AuctionId}; showing SQL values", auctionId);
                delayed = true;
            }
        }

        bids ??= await SqlBidsAsync(auctionId, ct);
        if (watchers < 0)
        {
            watchers = await db.Watchlist.CountAsync(w => w.AuctionId == auctionId, ct);
        }

        var increments = IncrementTable.FromMoney(view.IncrementTiers.Select(t => (t.FromPrice, t.Increment)));
        var minNext = view.IncrementTiers.Count == 0
            ? price
            : increments.MinNext(IncrementTable.ToCents(price), bidCount, IncrementTable.ToCents(auction.StartingPrice)) / 100m;

        // Handles for every bidder on the page, masked unless it is the viewer (or an admin is looking).
        var userIds = bids.Select(b => b.User).Append(leaderId ?? Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.UserId))
            .Select(u => new { u.UserId, u.Username })
            .ToDictionaryAsync(u => u.UserId, u => u.Username, ct);

        string HandleFor(Guid userId)
        {
            var name = names.GetValueOrDefault(userId);
            return viewerIsAdmin || userId == viewerId ? name ?? "unknown" : HandleMask.Mask(name);
        }

        var bidRows = bids.Select(b => new BidRowView(b.Seq, b.User, HandleFor(b.User), b.Amount, b.AtMs, b.Auto)).ToList();
        var leaderHandle = leaderId == null ? null : HandleFor(leaderId.Value);

        var myStatus = await MyStatusAsync(auctionId, viewerId, status, leaderId, bidRows, minNext, delayed, ct);

        var watching = viewerId != null && await db.Watchlist.AnyAsync(w => w.UserId == viewerId && w.AuctionId == auctionId, ct);   // spec 07

        return new AuctionDetailData(view, status, price, bidCount, endTime, seq, extUsedSec, watchers, minNext,
            leaderId, leaderHandle, bidRows, myStatus, delayed, isSellerView, watching);
    }

    private async Task<MyBidView?> MyStatusAsync(Guid auctionId, Guid? viewerId, AuctionStatus status, Guid? leaderId,
        List<BidRowView> rows, decimal minNext, bool redisDown, CancellationToken ct)
    {
        if (viewerId == null)
        {
            return null;
        }

        long? myMaxC = null;
        if (!redisDown && status is AuctionStatus.Live or AuctionStatus.Sealed)
        {
            try
            {
                myMaxC = await live.ReadMyMaxAsync(auctionId, viewerId.Value);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                log.LogWarning(ex, "Could not read my max for {AuctionId}", auctionId);
            }
        }

        decimal? myMax = myMaxC / 100m;
        if (myMax == null)
        {
            myMax = await db.Bids.AsNoTracking()
                .Where(b => b.AuctionId == auctionId && b.UserId == viewerId && b.VoidedAt == null && b.MaxAmount != null)
                .MaxAsync(b => b.MaxAmount, ct);
        }

        var hasBid = myMax != null || rows.Any(r => r.BidderId == viewerId);
        return hasBid ? new MyBidView(myMax, leaderId == viewerId, minNext) : null;
    }

    private async Task<List<(long Seq, Guid User, decimal Amount, long AtMs, bool Auto)>> SqlBidsAsync(Guid auctionId, CancellationToken ct)
    {
        var rows = await db.Bids.AsNoTracking()
            .Where(b => b.AuctionId == auctionId && b.VoidedAt == null && b.Kind != BidKind.MaxRaise)
            .OrderByDescending(b => b.ESeq).ThenByDescending(b => b.Idx)
            .Take(LiveStateReader.RecentBids)
            .Select(b => new { Seq = b.Seq ?? 0, b.UserId, b.Amount, b.AcceptedAt, b.Kind })
            .ToListAsync(ct);

        return rows.Select(r => (r.Seq, r.UserId, r.Amount,
            new DateTimeOffset(DateTime.SpecifyKind(r.AcceptedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            r.Kind is BidKind.ProxyAuto or BidKind.AutoOutbid)).ToList();
    }

    private static bool IsAuto(string kind) =>
        string.Equals(kind, nameof(BidKind.ProxyAuto), StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, nameof(BidKind.AutoOutbid), StringComparison.OrdinalIgnoreCase);
}
