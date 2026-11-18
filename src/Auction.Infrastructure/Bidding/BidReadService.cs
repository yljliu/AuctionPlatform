using System.Text;
using Auction.Domain.Bidding;
using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Domain.Identity;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Redis;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Bidding;

/// <summary>One bid history row as this viewer may see it. Never holds a max.</summary>
public sealed record BidHistoryRow(long Seq, Guid BidderId, string Handle, decimal Amount, long AtMs, bool Auto, bool Voided);

public sealed record BidHistory(IReadOnlyList<BidHistoryRow> Rows, int BidCount, string Currency);

public sealed record MyBidStatusView(decimal? MyMax, bool IsLeader, decimal MinNext, string Currency);

public enum MyBidsFilter
{
    Active,
    Won,
    Lost,
    All,
}

public sealed record MyBidRowView(AuctionCardRow Card, decimal? MyMax, decimal? MyLastAmount, bool IsLeader, string Outcome);

/// <summary>Rules shared by ListMyBids and its tests.</summary>
public static class MyBidRules
{
    /// <summary>"Won" or "Lost" once settled; empty while the auction is open.</summary>
    public static string OutcomeFor(AuctionStatus status, AuctionOutcome? outcome, Guid? leaderId, Guid userId)
    {
        if (status != AuctionStatus.Settled)
        {
            return "";
        }

        return outcome == AuctionOutcome.Sold && leaderId == userId ? "Won" : "Lost";
    }
}

/// <summary>
/// Read calls of BiddingService (spec 03 task 10, design 7): bid history, my status, my bids.
/// Max amounts are only ever returned to their own bidder.
/// </summary>
public sealed class BidReadService(
    AuctionDbContext db,
    LiveStateReader live,
    IncrementTableRepository increments,
    IConnectionMultiplexer redis,
    RedisOptions options,
    ILogger<BidReadService> log)
{
    private static readonly AuctionStatus[] HiddenFromPublic = [AuctionStatus.Draft, AuctionStatus.Cancelled];

    // ---------- GetBidHistory (R10.1) ----------

    public async Task<BidHistory> GetHistoryAsync(Guid auctionId, Guid? viewerId, bool viewerIsAdmin, bool includeVoided, Paging paging,
        CancellationToken ct = default)
    {
        if (includeVoided && !viewerIsAdmin)
        {
            throw new DomainException(ErrorCodes.RoleRequired, "Only admins can see voided bids.");
        }

        var auction = await db.Auctions.AsNoTracking()
            .Where(a => a.AuctionId == auctionId)
            .Select(a => new { a.Status, a.BidCount, a.Currency, a.SellerId })
            .FirstOrDefaultAsync(ct);
        if (auction == null || (HiddenFromPublic.Contains(auction.Status) && !viewerIsAdmin && viewerId != auction.SellerId))
        {
            throw new DomainException(ErrorCodes.NotFound, "Auction not found.");
        }

        List<(long Seq, Guid User, decimal Amount, long AtMs, bool Auto, bool Voided)>? rows = null;
        var bidCount = auction.BidCount;

        // Page 1 of a running auction comes from Redis, so it shows bids not yet in SQL.
        if (auction.Status is AuctionStatus.Live or AuctionStatus.Sealed && paging.Page == 1 && !includeVoided)
        {
            try
            {
                var detail = await live.ReadDetailAsync(auctionId);
                if (detail != null)
                {
                    bidCount = detail.Value.State.BidCount;
                    rows = detail.Value.Bids.Take(paging.Size)
                        .Select(b => (b.Seq, b.User, b.AmountC / 100m, b.AtMs, IsAuto(b.Kind), false))
                        .ToList();
                }
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                log.LogWarning(ex, "Redis unavailable for bid history of {AuctionId}; using SQL", auctionId);
            }
        }

        if (rows == null)
        {
            var query = db.Bids.AsNoTracking().Where(b => b.AuctionId == auctionId && b.Kind != BidKind.MaxRaise);
            if (!includeVoided)
            {
                query = query.Where(b => b.VoidedAt == null);
            }

            var sqlRows = await query
                .OrderByDescending(b => b.ESeq).ThenByDescending(b => b.Idx)
                .Skip(paging.Skip).Take(paging.Size)
                .Select(b => new { Seq = b.Seq ?? 0, b.UserId, b.Amount, b.AcceptedAt, b.Kind, Voided = b.VoidedAt != null })
                .ToListAsync(ct);

            rows = sqlRows.Select(r => (r.Seq, r.UserId, r.Amount, ToMs(r.AcceptedAt),
                r.Kind is BidKind.ProxyAuto or BidKind.AutoOutbid, r.Voided)).ToList();
        }

        var names = await NamesAsync(rows.Select(r => r.User), ct);
        var result = rows
            .Select(r => new BidHistoryRow(r.Seq, r.User, HandleMask.For(names.GetValueOrDefault(r.User), r.User, viewerId, viewerIsAdmin),
                r.Amount, r.AtMs, r.Auto, r.Voided))
            .ToList();

        return new BidHistory(result, bidCount, auction.Currency);
    }

    // ---------- GetMyBidStatus (R10.2) ----------

    public async Task<MyBidStatusView> GetMyStatusAsync(Guid auctionId, Guid userId, CancellationToken ct = default)
    {
        var keys = RedisKeys.For(auctionId, options);
        try
        {
            var database = redis.GetDatabase();
            var stateTask = database.HashGetAsync(keys.State, ["leader", "priceC", "bidCount", "startC", "inc", "cur"]);
            var maxTask = database.HashGetAsync(keys.Max, userId.ToString());
            await Task.WhenAll(stateTask, maxTask);
            var s = stateTask.Result;

            if (!s[4].IsNullOrEmpty)
            {
                var table = IncrementTable.Parse(s[4].ToString());
                var minNextC = table.MinNext((long)s[1], (int)s[2], (long)s[3]);
                var myMax = maxTask.Result.IsNullOrEmpty ? (decimal?)null : (long)maxTask.Result / 100m;
                var isLeader = Guid.TryParse(s[0].ToString(), out var leader) && leader == userId;
                return new MyBidStatusView(myMax, isLeader, minNextC / 100m, s[5].IsNullOrEmpty ? "USD" : s[5].ToString());
            }
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException or FormatException)
        {
            log.LogWarning(ex, "Could not read live state of {AuctionId}; using SQL", auctionId);
        }

        // Not live in Redis (or Redis down): answer from SQL.
        var auction = await db.Auctions.AsNoTracking().FirstOrDefaultAsync(a => a.AuctionId == auctionId, ct)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Auction not found.");

        var mine = await db.Bids.AsNoTracking()
            .Where(b => b.AuctionId == auctionId && b.UserId == userId && b.VoidedAt == null)
            .MaxAsync(b => b.MaxAmount, ct);

        var priceC = IncrementTable.ToCents(auction.CurrentPrice ?? auction.StartingPrice);
        var tableForAuction = await increments.LoadAsync(auction.IncrementTableId, ct);
        var minNext = tableForAuction.MinNext(priceC, auction.BidCount, IncrementTable.ToCents(auction.StartingPrice)) / 100m;

        return new MyBidStatusView(mine, auction.CurrentLeaderId == userId, minNext, auction.Currency);
    }

    // ---------- ListMyBids (R10.3) ----------

    private sealed class MyBidSqlRow
    {
        public Guid AuctionId { get; set; }
        public Guid ItemId { get; set; }
        public string Title { get; set; } = "";
        public Guid? ThumbId { get; set; }
        public int CategoryId { get; set; }
        public ItemCondition Condition { get; set; }
        public AuctionStatus Status { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal Price { get; set; }
        public int BidCount { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public decimal? ReservePrice { get; set; }
        public decimal? BuyNowPrice { get; set; }
        public decimal? MyMax { get; set; }
        public decimal? MyLastAmount { get; set; }
        public Guid? CurrentLeaderId { get; set; }
        public AuctionOutcome? Outcome { get; set; }
    }

    public async Task<(IReadOnlyList<MyBidRowView> Rows, int Total)> ListMineAsync(Guid userId, MyBidsFilter filter, Paging paging,
        CancellationToken ct = default)
    {
        var where = filter switch
        {
            MyBidsFilter.Active => "a.Status IN (1, 2, 3, 4, 7)",
            MyBidsFilter.Won => "a.Status = 5 AND a.Outcome = 0 AND a.CurrentLeaderId = @UserId",
            MyBidsFilter.Lost => "a.Status = 5 AND (a.Outcome IS NULL OR a.Outcome <> 0 OR a.CurrentLeaderId IS NULL OR a.CurrentLeaderId <> @UserId)",
            _ => "1 = 1",
        };

        var sql = new StringBuilder($"""
            WITH mine AS (
                SELECT b.AuctionId, MAX(b.MaxAmount) AS MyMax
                FROM bidding.Bids b
                WHERE b.UserId = @UserId AND b.VoidedAt IS NULL
                GROUP BY b.AuctionId)
            SELECT a.AuctionId, a.ItemId, i.Title, img.ImageId AS ThumbId, i.CategoryId, i.Condition, a.Status, a.Currency,
                   COALESCE(a.CurrentPrice, a.StartingPrice) AS Price, a.BidCount, a.StartTime, a.EndTime, a.ReservePrice, a.BuyNowPrice,
                   m.MyMax, last.Amount AS MyLastAmount, a.CurrentLeaderId, a.Outcome
            FROM mine m
            JOIN catalog.Auctions a ON a.AuctionId = m.AuctionId
            JOIN catalog.Items i ON i.ItemId = a.ItemId
            OUTER APPLY (SELECT TOP 1 ImageId FROM catalog.ItemImages WHERE ItemId = i.ItemId AND Status = 1 ORDER BY SortOrder) img
            OUTER APPLY (SELECT TOP 1 b.Amount FROM bidding.Bids b
                         WHERE b.AuctionId = a.AuctionId AND b.UserId = @UserId AND b.VoidedAt IS NULL
                         ORDER BY b.ESeq DESC, b.Idx DESC) last
            WHERE {where}
            ORDER BY a.EndTime DESC, a.AuctionId
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;

            SELECT COUNT(*) FROM (SELECT DISTINCT b.AuctionId FROM bidding.Bids b WHERE b.UserId = @UserId AND b.VoidedAt IS NULL) m
            JOIN catalog.Auctions a ON a.AuctionId = m.AuctionId
            WHERE {where};
            """);

        var connection = db.Database.GetDbConnection();
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(sql.ToString(),
            new { UserId = userId, Skip = paging.Skip, Take = paging.Size }, cancellationToken: ct));
        var rows = (await results.ReadAsync<MyBidSqlRow>()).ToList();
        var total = await results.ReadSingleAsync<int>();

        // Live numbers and the live leader for running auctions.
        var states = new Dictionary<Guid, LiveState>();
        var liveIds = rows.Where(r => r.Status == AuctionStatus.Live).Select(r => r.AuctionId).ToList();
        if (liveIds.Count > 0)
        {
            try
            {
                foreach (var (id, state) in await live.ReadManyAsync(liveIds))
                {
                    states[id] = state;
                }
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                log.LogWarning(ex, "Redis unavailable for ListMyBids; showing SQL values");
            }
        }

        var views = new List<MyBidRowView>();
        foreach (var row in rows)
        {
            var card = new AuctionCardRow
            {
                AuctionId = row.AuctionId, ItemId = row.ItemId, Title = row.Title, ThumbId = row.ThumbId, CategoryId = row.CategoryId,
                Condition = row.Condition, Status = row.Status, Currency = row.Currency, Price = row.Price, BidCount = row.BidCount,
                StartTime = row.StartTime, EndTime = row.EndTime, ReservePrice = row.ReservePrice, BuyNowPrice = row.BuyNowPrice,
            };

            var leader = row.CurrentLeaderId;
            if (states.TryGetValue(row.AuctionId, out var state))
            {
                AuctionBrowser.ApplyLive(card, state);
                leader = state.Leader;
            }

            views.Add(new MyBidRowView(card, row.MyMax, row.MyLastAmount, leader == userId,
                MyBidRules.OutcomeFor(card.Status, row.Outcome, leader, userId)));
        }

        return (views, total);
    }

    // ---------- helpers ----------

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, u => u.Username, ct);
    }

    private static bool IsAuto(string kind) =>
        string.Equals(kind, nameof(BidKind.ProxyAuto), StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, nameof(BidKind.AutoOutbid), StringComparison.OrdinalIgnoreCase);

    private static long ToMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
