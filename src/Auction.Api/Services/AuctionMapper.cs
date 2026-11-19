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

    // ---------- auction page (task 9) ----------

    /// <summary>The owner and admin view: the public detail plus private fields.</summary>
    public static SellerAuction ToSellerAuction(AuctionDetailData data)
    {
        var auction = data.View.Auction;
        return new SellerAuction
        {
            Detail = ToDetail(data),
            ReservePrice = auction.ReservePrice == null ? null : Money(auction.ReservePrice.Value, auction.Currency),
            FeeScheduleId = auction.FeeScheduleId,
            IncrementTableId = auction.IncrementTableId,
            Outcome = auction.Outcome?.ToString() ?? "",
            CancelledReason = auction.CancelledReason ?? "",
            RowVersion = Convert.ToBase64String(auction.RowVersion),
        };
    }

    /// <summary>
    /// The public auction page. Built field by field from <see cref="AuctionDetailData"/>, which holds
    /// no leader max and only "reserve met or not", so neither can leak.
    /// </summary>
    public static AuctionDetail ToDetail(AuctionDetailData data)
    {
        var view = data.View;
        var auction = view.Auction;
        var currency = auction.Currency;

        var card = ToCard(view);
        card.Status = data.Status.ToString();
        card.CurrentPrice = Money(data.Price, currency);
        card.BidCount = data.BidCount;
        card.EndTimeMs = CatalogMapper.ToUnixMs(data.EndTime);
        card.ReserveMet = data.ReserveMet;
        card.PricesDelayed = data.PricesDelayed;
        card.Watching = data.Watching;   // spec 07

        var detail = new AuctionDetail
        {
            Card = card,
            Item = CatalogMapper.ToProto(new ItemWithImages(view.Item, view.Images)),
            Seller = ToSeller(view),
            StartingPrice = Money(auction.StartingPrice, currency),
            MinNextBid = Money(data.MinNext, currency),
            ScheduledEndTimeMs = CatalogMapper.ToUnixMs(auction.ScheduledEndTime),
            SnipeWindowSec = auction.SnipeWindowSec,
            ExtensionSec = auction.ExtensionSec,
            MaxTotalExtensionSec = auction.MaxTotalExtensionSec,
            ExtensionUsedSec = data.ExtensionUsedSec,
            Leader = data.LeaderId == null ? null : new PublicUser { UserId = data.LeaderId.Value.ToString(), Handle = data.LeaderHandle ?? "" },
            Watchers = data.Watchers,
            Seq = data.Seq,
        };

        foreach (var bid in data.RecentBids)
        {
            detail.RecentBids.Add(new PublicBidRow
            {
                Seq = bid.Seq,
                Bidder = new PublicUser { UserId = bid.BidderId.ToString(), Handle = bid.Handle },
                Amount = Money(bid.Amount, currency),
                AtMs = bid.AtMs,
                Auto = bid.Auto,
            });
        }

        foreach (var tier in view.IncrementTiers)
        {
            detail.IncrementTiers.Add(new IncrementTier { From = Money(tier.FromPrice, currency), Increment = Money(tier.Increment, currency) });
        }

        if (data.MyStatus != null)
        {
            detail.MyStatus = new MyBidStatus
            {
                MyMax = data.MyStatus.MyMax == null ? null : Money(data.MyStatus.MyMax.Value, currency),
                IsLeader = data.MyStatus.IsLeader,
                MinNextBid = Money(data.MyStatus.MinNext, currency),
            };
        }

        return detail;
    }

    // ---------- browse (task 7 and 8) ----------

    /// <summary>A browse row to a card, with the watching and prices-delayed flags.</summary>
    public static AuctionCard ToCard(AuctionCardRow row, bool watching, bool pricesDelayed)
    {
        var card = ToCard(row);
        card.Watching = watching;
        card.PricesDelayed = pricesDelayed;
        return card;
    }

    /// <summary>A browse row to a card. The reserve price only decides reserve_met and is then dropped.</summary>
    public static AuctionCard ToCard(AuctionCardRow row) => new()
    {
        AuctionId = row.AuctionId.ToString(),
        ItemId = row.ItemId.ToString(),
        Title = row.Title,
        ThumbUrl = row.ThumbId == null ? "" : $"/img/{row.ThumbId}/thumb",
        CategoryId = row.CategoryId,
        Condition = row.Condition.ToString(),
        Status = row.Status.ToString(),
        Currency = row.Currency,
        CurrentPrice = Money(row.Price, row.Currency),
        BidCount = row.BidCount,
        StartTimeMs = CatalogMapper.ToUnixMs(row.StartTime),
        EndTimeMs = CatalogMapper.ToUnixMs(row.EndTime),
        ReserveMet = row.ReservePrice == null ? null : row.BidCount > 0 && row.Price >= row.ReservePrice.Value,
        BuyNowPrice = row.BuyNowPrice == null ? null : Money(row.BuyNowPrice.Value, row.Currency),
    };

    /// <summary>One settlement row (spec 06). showBuyer: the seller sees the buyer's masked handle.</summary>
    public static SettlementRow ToSettlementRow(Infrastructure.Account.SettlementListRow row, bool showBuyer)
    {
        var currency = row.Card.Currency;
        return new SettlementRow
        {
            SettlementId = row.SettlementId.ToString(),
            Auction = ToCard(row.Card),
            Buyer = showBuyer ? new PublicUser { UserId = row.BuyerId.ToString(), Handle = row.BuyerHandle } : null,
            HammerPrice = Money(row.HammerPrice, currency),
            BuyerPremium = Money(row.BuyerPremium, currency),
            ShippingCost = Money(row.ShippingCost, currency),
            TotalDue = Money(row.TotalDue, currency),
            AmountPaid = Money(row.AmountPaid, currency),
            SellerFee = Money(row.SellerFee, currency),
            Status = row.Status.ToString(),
            PaymentDeadlineMs = CatalogMapper.ToUnixMs(row.PaymentDeadline),
            CreatedAtMs = CatalogMapper.ToUnixMs(row.CreatedAt),
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
