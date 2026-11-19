using Auction.Domain.Enums;

namespace Auction.Domain.Settlements;

/// <summary>The money owed for a Sold auction (spec 05 R5.5). Tax is 0 in the MVP.</summary>
public sealed record SettlementAmounts(
    decimal HammerPrice,
    decimal BuyerPremium,
    decimal ShippingCost,
    decimal Tax,
    decimal TotalDue,
    decimal SellerFee);

/// <summary>Who wins and what they owe (spec 05 design 6). Pure, so every rule has a unit test.</summary>
public static class SettlementMath
{
    /// <summary>The payment window after the auction settles (R5.5).</summary>
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromHours(72);

    /// <summary>No bids, or price below the reserve: Unsold. Otherwise Sold to the leader (R5.4).</summary>
    public static AuctionOutcome Decide(int bidCount, decimal? currentPrice, decimal? reservePrice, Guid? leaderId)
    {
        if (bidCount == 0 || currentPrice == null || leaderId == null)
        {
            return AuctionOutcome.Unsold;
        }

        return reservePrice != null && currentPrice.Value < reservePrice.Value ? AuctionOutcome.Unsold : AuctionOutcome.Sold;
    }

    /// <summary>
    /// premium = round(price * buyerPremiumPct / 100, 2); shipping = item cost when the buyer pays, else 0;
    /// total = price + premium + shipping; sellerFee = round(price * sellerPct / 100, 2). Halves round away from zero.
    /// </summary>
    public static SettlementAmounts Compute(decimal price, decimal buyerPremiumPct, decimal sellerFinalValuePct,
        ShippingType shippingType, decimal? itemShippingCost)
    {
        var premium = Round(price * buyerPremiumPct / 100m);
        var shipping = shippingType == ShippingType.BuyerPays ? itemShippingCost ?? 0m : 0m;
        const decimal tax = 0m;
        var sellerFee = Round(price * sellerFinalValuePct / 100m);
        return new SettlementAmounts(price, premium, shipping, tax, price + premium + shipping + tax, sellerFee);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
