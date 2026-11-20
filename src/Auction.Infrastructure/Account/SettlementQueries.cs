using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Identity;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace Auction.Infrastructure.Account;

/// <summary>One settlement with its auction card (spec 06: purchases and sales pages).</summary>
public sealed class SettlementListRow
{
    public Guid SettlementId { get; set; }
    public Guid BuyerId { get; set; }
    public string BuyerUsername { get; set; } = "";
    public decimal HammerPrice { get; set; }
    public decimal BuyerPremium { get; set; }
    public decimal ShippingCost { get; set; }
    public decimal TotalDue { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal SellerFee { get; set; }
    public SettlementStatus Status { get; set; }
    public DateTime PaymentDeadline { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid SellerId { get; set; }
    public Guid? PaymentId { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? CardLast4 { get; set; }
    public Guid? OfferId { get; set; }
    public byte? OfferStatus { get; set; }
    public Guid? OfferBidderId { get; set; }
    public decimal? OfferAmount { get; set; }
    public DateTime? OfferExpiresAt { get; set; }

    /// <summary>Defaulted, no offer yet, within 7 days, and an active other bidder exists (spec 10 R6.1, R6.2).</summary>
    public bool CanSecondChance { get; set; }

    public AuctionCardRow Card { get; set; } = new();

    /// <summary>The buyer as others see them (the seller sees a masked handle).</summary>
    public string BuyerHandle => HandleMask.Mask(BuyerUsername);
}

public sealed record SettlementPage(IReadOnlyList<SettlementListRow> Rows, int Total);

/// <summary>Settlements for one buyer or one seller, newest first (spec 06, R4.3 and R5.4).</summary>
public sealed class SettlementQueries(AuctionDbContext db)
{
    private const string Select = """
        SELECT s.SettlementId, s.BuyerId, u.Username AS BuyerUsername, s.HammerPrice, s.BuyerPremium, s.ShippingCost,
               s.TotalDue, s.AmountPaid, s.SellerFee, s.Status, s.PaymentDeadline, s.CreatedAt,
               s.SellerId, p.PaymentId, p.CreatedAt AS PaidAt, p.CardLast4,
               sco.OfferId, sco.Status AS OfferStatus, sco.BidderId AS OfferBidderId, sco.Amount AS OfferAmount, sco.ExpiresAt AS OfferExpiresAt,
               CASE WHEN s.Status = 5 AND sco.OfferId IS NULL AND s.UpdatedAt > DATEADD(DAY, -7, SYSUTCDATETIME())
                         AND EXISTS (SELECT 1 FROM bidding.Bids b JOIN ident.Users nb ON nb.UserId = b.UserId
                                     WHERE b.AuctionId = s.AuctionId AND b.UserId <> s.BuyerId AND b.VoidedAt IS NULL AND nb.Status = 1)
                    THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS CanSecondChance,
               a.AuctionId, a.ItemId, i.Title, img.ImageId AS ThumbId, i.CategoryId, i.Condition, a.Status,
               a.Currency, COALESCE(a.CurrentPrice, a.StartingPrice) AS Price, a.BidCount, a.StartTime, a.EndTime,
               a.ReservePrice, a.BuyNowPrice
        FROM pay.Settlements s
        JOIN catalog.Auctions a ON a.AuctionId = s.AuctionId
        JOIN catalog.Items i ON i.ItemId = a.ItemId
        LEFT JOIN ident.Users u ON u.UserId = s.BuyerId
        LEFT JOIN pay.Payments p ON p.SettlementId = s.SettlementId
        LEFT JOIN pay.SecondChanceOffers sco ON sco.DefaultedSettlementId = s.SettlementId
        OUTER APPLY (SELECT TOP 1 ImageId FROM catalog.ItemImages WHERE ItemId = i.ItemId AND Status = 1 ORDER BY SortOrder) img
        """;

    public Task<SettlementPage> ForBuyerAsync(Guid buyerId, Paging paging, CancellationToken ct = default) =>
        RunAsync("s.BuyerId = @UserId", buyerId, paging, ct);

    public Task<SettlementPage> ForSellerAsync(Guid sellerId, Paging paging, CancellationToken ct = default) =>
        RunAsync("s.SellerId = @UserId", sellerId, paging, ct);

    /// <summary>One settlement with its card and payment, or null (spec 10 pay page and receipt).</summary>
    public async Task<SettlementListRow?> ByIdAsync(Guid settlementId, CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        var rows = await connection.QueryAsync<SettlementListRow, AuctionCardRow, SettlementListRow>(
            new CommandDefinition($"{Select}\nWHERE s.SettlementId = @Id", new { Id = settlementId }, cancellationToken: ct),
            (settlement, card) =>
            {
                settlement.Card = card;
                return settlement;
            },
            splitOn: "AuctionId");
        return rows.FirstOrDefault();
    }

    // The where text is one of two fixed strings above, never user input.
    private async Task<SettlementPage> RunAsync(string where, Guid userId, Paging paging, CancellationToken ct)
    {
        var sql = $"""
            {Select}
            WHERE {where}
            ORDER BY s.CreatedAt DESC, s.SettlementId
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;

            SELECT COUNT(*) FROM pay.Settlements s WHERE {where};
            """;

        var connection = db.Database.GetDbConnection();
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            sql, new { UserId = userId, paging.Skip, Take = paging.Size }, cancellationToken: ct));
        var rows = results.Read<SettlementListRow, AuctionCardRow, SettlementListRow>(
            (settlement, card) =>
            {
                settlement.Card = card;
                return settlement;
            },
            splitOn: "AuctionId").ToList();
        var total = await results.ReadSingleAsync<int>();
        return new SettlementPage(rows, total);
    }
}
