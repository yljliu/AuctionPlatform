using System.Text;
using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Infrastructure.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace Auction.Infrastructure.Catalog;

public enum AuctionSortOrder
{
    EndingSoon,
    Newest,
    PriceAsc,
    PriceDesc,
    MostBids,
}

/// <summary>Browse and search filters. Null means "no filter".</summary>
public sealed record AuctionSearch(
    AuctionStatus Status,
    IReadOnlyList<int>? CategoryIds = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    DateTime? EndingBefore = null,
    Guid? SellerId = null,
    IReadOnlyList<ItemCondition>? Conditions = null,
    IReadOnlyList<string>? Words = null,
    AuctionSortOrder Sort = AuctionSortOrder.EndingSoon);

/// <summary>One row of a browse result. ReservePrice is only used to work out reserve_met.</summary>
public sealed class AuctionCardRow
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
}

public sealed record AuctionCardPage(IReadOnlyList<AuctionCardRow> Rows, int Total);

/// <summary>
/// Browse and search SQL (design 5), run with Dapper. Every value is a parameter.
/// Only fixed SQL pieces are joined together, and the sort comes from a fixed list, never from user text.
/// </summary>
public sealed class AuctionQueries(AuctionDbContext db)
{
    /// <summary>Statuses the public may browse. Drafts and cancelled auctions stay hidden.</summary>
    public static readonly IReadOnlySet<AuctionStatus> BrowsableStatuses = new HashSet<AuctionStatus>
    {
        AuctionStatus.Scheduled, AuctionStatus.Live, AuctionStatus.Sealed, AuctionStatus.Settling, AuctionStatus.Settled,
    };

    private const string PriceExpr = "COALESCE(a.CurrentPrice, a.StartingPrice)";

    public async Task<AuctionCardPage> SearchAsync(AuctionSearch search, Paging paging, CancellationToken ct = default)
    {
        var where = new StringBuilder("WHERE a.Status = @Status");
        var args = new DynamicParameters();
        args.Add("Status", (byte)search.Status);

        if (search.CategoryIds != null)
        {
            where.Append(" AND i.CategoryId IN @CategoryIds");
            args.Add("CategoryIds", search.CategoryIds.Count > 0 ? search.CategoryIds : [-1]);
        }

        if (search.MinPrice != null)
        {
            where.Append($" AND {PriceExpr} >= @MinPrice");
            args.Add("MinPrice", search.MinPrice);
        }

        if (search.MaxPrice != null)
        {
            where.Append($" AND {PriceExpr} <= @MaxPrice");
            args.Add("MaxPrice", search.MaxPrice);
        }

        if (search.EndingBefore != null)
        {
            where.Append(" AND a.EndTime <= @EndingBefore");
            args.Add("EndingBefore", search.EndingBefore);
        }

        if (search.SellerId != null)
        {
            where.Append(" AND a.SellerId = @SellerId");
            args.Add("SellerId", search.SellerId);
        }

        if (search.Conditions is { Count: > 0 })
        {
            where.Append(" AND i.Condition IN @Conditions");
            // int, not byte: Dapper sends a byte[] as one binary value instead of a list.
            args.Add("Conditions", search.Conditions.Select(c => (int)c).ToArray());
        }

        if (search.Words != null)
        {
            // Every word must appear in the title or the description.
            for (var n = 0; n < search.Words.Count; n++)
            {
                where.Append($" AND (i.Title LIKE @Q{n} OR i.Description LIKE @Q{n})");
                args.Add($"Q{n}", SearchText.ContainsPattern(search.Words[n]));
            }
        }

        const string joins = "FROM catalog.Auctions a JOIN catalog.Items i ON i.ItemId = a.ItemId";
        const string firstImage = """
            OUTER APPLY (SELECT TOP 1 ImageId FROM catalog.ItemImages
                         WHERE ItemId = i.ItemId AND Status = 1 ORDER BY SortOrder) img
            """;

        // Query 1: one page of cards. Query 2: how many match in total.
        var sql = $"""
            SELECT a.AuctionId, a.ItemId, i.Title, img.ImageId AS ThumbId, i.CategoryId, i.Condition, a.Status,
                   a.Currency, {PriceExpr} AS Price, a.BidCount, a.StartTime, a.EndTime, a.ReservePrice, a.BuyNowPrice
            {joins}
            {firstImage}
            {where}
            ORDER BY {OrderBy(search.Sort)}
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;

            SELECT COUNT(*) {joins} {where};
            """;
        args.Add("Skip", paging.Skip);
        args.Add("Take", paging.Size);

        var connection = db.Database.GetDbConnection();
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(sql, args, cancellationToken: ct));
        var rows = (await results.ReadAsync<AuctionCardRow>()).ToList();
        var total = await results.ReadSingleAsync<int>();

        return new AuctionCardPage(rows, total);
    }

    /// <summary>Card rows for these auctions, in the order given (spec 07: the watchlist).</summary>
    public async Task<IReadOnlyList<AuctionCardRow>> ByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var sql = $"""
            SELECT a.AuctionId, a.ItemId, i.Title, img.ImageId AS ThumbId, i.CategoryId, i.Condition, a.Status,
                   a.Currency, {PriceExpr} AS Price, a.BidCount, a.StartTime, a.EndTime, a.ReservePrice, a.BuyNowPrice
            FROM catalog.Auctions a JOIN catalog.Items i ON i.ItemId = a.ItemId
            OUTER APPLY (SELECT TOP 1 ImageId FROM catalog.ItemImages WHERE ItemId = i.ItemId AND Status = 1 ORDER BY SortOrder) img
            WHERE a.AuctionId IN @Ids;
            """;
        var rows = (await db.Database.GetDbConnection().QueryAsync<AuctionCardRow>(new CommandDefinition(sql, new { Ids = ids }, cancellationToken: ct)))
            .ToDictionary(r => r.AuctionId);
        return ids.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
    }

    // A fixed list. The last column makes the order stable so pages never repeat or skip rows.
    private static string OrderBy(AuctionSortOrder sort) => sort switch
    {
        AuctionSortOrder.Newest => "a.CreatedAt DESC, a.AuctionId",
        AuctionSortOrder.PriceAsc => $"{PriceExpr} ASC, a.AuctionId",
        AuctionSortOrder.PriceDesc => $"{PriceExpr} DESC, a.AuctionId",
        AuctionSortOrder.MostBids => "a.BidCount DESC, a.EndTime ASC, a.AuctionId",
        _ => "a.EndTime ASC, a.AuctionId",
    };
}
