using Auction.Domain.Enums;
using Auction.Domain.Settlements;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 05 task 4: who wins and what they owe (R5.4, R5.5).</summary>
public class SettlementMathTests
{
    private static readonly Guid Leader = Guid.NewGuid();

    [Fact]
    public void No_bids_is_unsold() =>
        SettlementMath.Decide(0, null, null, null).Should().Be(AuctionOutcome.Unsold);

    [Fact]
    public void Below_reserve_is_unsold() =>
        SettlementMath.Decide(3, 99.99m, 100m, Leader).Should().Be(AuctionOutcome.Unsold);

    [Fact]
    public void Exactly_at_reserve_is_sold() =>
        SettlementMath.Decide(3, 100m, 100m, Leader).Should().Be(AuctionOutcome.Sold);

    [Fact]
    public void No_reserve_with_bids_is_sold() =>
        SettlementMath.Decide(1, 10m, null, Leader).Should().Be(AuctionOutcome.Sold);

    [Fact]
    public void Bids_but_no_leader_is_unsold() =>
        SettlementMath.Decide(1, 10m, null, null).Should().Be(AuctionOutcome.Unsold);

    [Fact]
    public void Sold_totals_with_5_percent_premium_shipping_and_8_percent_seller_fee()
    {
        var m = SettlementMath.Compute(250m, 5m, 8m, ShippingType.BuyerPays, 15m);

        m.Should().Be(new SettlementAmounts(HammerPrice: 250m, BuyerPremium: 12.50m, ShippingCost: 15m, Tax: 0m,
            TotalDue: 277.50m, SellerFee: 20m));
    }

    [Theory]
    [InlineData(ShippingType.FreeShipping)]
    [InlineData(ShippingType.LocalPickupOnly)]
    public void Shipping_is_0_unless_the_buyer_pays(ShippingType type)
    {
        var m = SettlementMath.Compute(100m, 5m, 8m, type, 15m);

        m.ShippingCost.Should().Be(0m);
        m.TotalDue.Should().Be(105m);
    }

    [Fact]
    public void Buyer_pays_with_no_cost_set_is_0() =>
        SettlementMath.Compute(100m, 5m, 8m, ShippingType.BuyerPays, null).ShippingCost.Should().Be(0m);

    [Fact]
    public void Half_cents_round_away_from_zero()
    {
        // 5 % of 10.10 = 0.505 -> 0.51 ; 8 % of 10.10 = 0.808 -> 0.81
        var m = SettlementMath.Compute(10.10m, 5m, 8m, ShippingType.FreeShipping, null);

        m.BuyerPremium.Should().Be(0.51m);
        m.SellerFee.Should().Be(0.81m);
        m.TotalDue.Should().Be(10.61m);
    }

    [Fact]
    public void Payment_window_is_72_hours() =>
        SettlementMath.PaymentWindow.Should().Be(TimeSpan.FromHours(72));
}
