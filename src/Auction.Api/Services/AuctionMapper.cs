using Auction.Contracts;
using Auction.Domain.Enums;
using Auction.Infrastructure.Catalog;

namespace Auction.Api.Services;

/// <summary>
/// Builds auction messages from SQL data. Live price, leader and bids from Redis are added in tasks 8 and 9.
/// The public AuctionDetail never gets the reserve price.
/// </summary>
public static class AuctionMapper
{
    public static SellerAuction ToSellerAuction(AuctionView view)
    {
        var auction = view.Auction;
        return new SellerAuction
        {
            Detail = ToDetail(view),
            ReservePrice = auction.ReservePrice == null ? null : Money(auction.ReservePrice.Value, auction.Currency),
            FeeScheduleId = auction.FeeScheduleId,
            IncrementTableId = auction.IncrementTableId,
            Outcome = auction.Outcome?.ToString() ?? "",
            CancelledReason = auction.CancelledReason ?? "",
            RowVersion = Convert.ToBase64String(auction.RowVersion),
        };
    }

    public static AuctionDetail ToDetail(AuctionView view)
    {
        var auction = view.Auction;
        var detail = new AuctionDetail
        {
            Card = ToCard(view),
            Item = CatalogMapper.ToProto(new ItemWithImages(view.Item, view.Images)),
            Seller = ToSeller(view),
            StartingPrice = Money(auction.StartingPrice, auction.Currency),
            MinNextBid = Money(auction.BidCount == 0 ? auction.StartingPrice : auction.CurrentPrice ?? auction.StartingPrice, auction.Currency),
            ScheduledEndTimeMs = CatalogMapper.ToUnixMs(auction.ScheduledEndTime),
            SnipeWindowSec = auction.SnipeWindowSec,
            ExtensionSec = auction.ExtensionSec,
            MaxTotalExtensionSec = auction.MaxTotalExtensionSec,
            ExtensionUsedSec = (int)Math.Max(0, (auction.EndTime - auction.ScheduledEndTime).TotalSeconds),
            Seq = auction.LastSeq,
        };

        foreach (var tier in view.IncrementTiers)
        {
            detail.IncrementTiers.Add(new IncrementTier
            {
                From = Money(tier.FromPrice, auction.Currency),
                Increment = Money(tier.Increment, auction.Currency),
            });
        }

        return detail;
    }

    public static AuctionCard ToCard(AuctionView view)
    {
        var auction = view.Auction;
        var price = auction.CurrentPrice ?? auction.StartingPrice;
        var thumb = view.Images.Count > 0 ? $"/img/{view.Images[0].ImageId}/thumb" : "";

        return new AuctionCard
        {
            AuctionId = auction.AuctionId.ToString(),
            ItemId = auction.ItemId.ToString(),
            Title = view.Item.Title,
            ThumbUrl = thumb,
            CategoryId = view.Item.CategoryId,
            Condition = view.Item.Condition.ToString(),
            Status = auction.Status.ToString(),
            Currency = auction.Currency,
            CurrentPrice = Money(price, auction.Currency),
            BidCount = auction.BidCount,
            StartTimeMs = CatalogMapper.ToUnixMs(auction.StartTime),
            EndTimeMs = CatalogMapper.ToUnixMs(auction.EndTime),
            // Only whether the reserve is met, never the reserve itself.
            ReserveMet = auction.ReservePrice == null ? null : auction.BidCount > 0 && price >= auction.ReservePrice.Value,
            BuyNowPrice = auction.BuyNowPrice == null ? null : Money(auction.BuyNowPrice.Value, auction.Currency),
        };
    }

    private static SellerSummary ToSeller(AuctionView view)
    {
        var profile = view.Seller;
        return new SellerSummary
        {
            UserId = view.Auction.SellerId.ToString(),
            DisplayName = profile?.DisplayName ?? "",
            IsVerified = profile?.Status == SellerStatus.Verified,
            RatingAvg = profile == null || profile.RatingCount == 0 ? 0 : (double)profile.RatingSum / profile.RatingCount,
            RatingCount = profile?.RatingCount ?? 0,
            CompletedSales = profile?.CompletedSales ?? 0,
        };
    }

    private static Contracts.Money Money(decimal amount, string currency) => new Domain.Money(amount, currency).ToProto();

    /// <summary>Proto fields to the rule input. Missing optional fields become null (use the default).</summary>
    public static Domain.Catalog.AuctionInput ToInput(AuctionFields? fields)
    {
        fields ??= new AuctionFields();
        var currency = !string.IsNullOrWhiteSpace(fields.Currency) ? fields.Currency : fields.StartingPrice?.Currency;

        return new Domain.Catalog.AuctionInput(
            DateTimeOffset.FromUnixTimeMilliseconds(fields.StartTimeMs).UtcDateTime,
            DateTimeOffset.FromUnixTimeMilliseconds(fields.EndTimeMs).UtcDateTime,
            fields.StartingPrice?.ToDomain().Amount ?? 0m,
            fields.ReservePrice?.ToDomain().Amount,
            fields.SnipeWindowSec,
            fields.ExtensionSec,
            fields.MaxTotalExtensionSec,
            null,
            currency);
    }
}
