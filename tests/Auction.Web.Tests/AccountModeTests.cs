using System.Security.Claims;
using Auction.Contracts;
using Auction.Web.Auth;
using Auction.Web.Services.Ui;
using FluentAssertions;
using Xunit;

namespace Auction.Web.Tests;

/// <summary>Bidder or seller mode, and what the menu shows (design W9).</summary>
public class AccountModeTests
{
    private static ClaimsPrincipal User(string? mode, params string[] roles)
    {
        var me = new Me { UserId = Guid.NewGuid().ToString(), Username = "alice" };
        me.Roles.AddRange(roles);
        return AccountEndpoints.BuildPrincipal(me, "sid1", mode);
    }

    [Theory]
    [InlineData(true, null, "Seller")]
    [InlineData(true, "Seller", "Seller")]
    [InlineData(true, "Bidder", "Bidder")]
    [InlineData(true, "bidder", "Bidder")]
    [InlineData(false, null, "Bidder")]
    [InlineData(false, "Seller", "Bidder")]
    public void Only_a_seller_can_be_in_seller_mode(bool isSeller, string? chosen, string mode) =>
        AccountMode.Resolve(isSeller, chosen).Should().Be(mode);

    [Fact]
    public void A_seller_in_seller_mode_sees_the_seller_hub()
    {
        var user = User(null, "Bidder", "Seller");

        AccountMode.Of(user).Should().Be(AccountMode.Seller);
        AccountMode.ShowSellerHub(user).Should().BeTrue();
        AccountMode.ShowBecomeSeller(user).Should().BeFalse();
    }

    [Fact]
    public void A_seller_in_bidder_mode_does_not()
    {
        var user = User("Bidder", "Bidder", "Seller");

        AccountMode.ShowSellerHub(user).Should().BeFalse();
        AccountMode.ShowBecomeSeller(user).Should().BeFalse();
    }

    [Fact]
    public void A_bidder_sees_become_a_seller_and_cannot_pick_seller_mode()
    {
        var user = User("Seller", "Bidder");

        AccountMode.Of(user).Should().Be(AccountMode.Bidder);
        AccountMode.ShowSellerHub(user).Should().BeFalse();
        AccountMode.ShowBecomeSeller(user).Should().BeTrue();
    }

    [Fact]
    public void The_switch_goes_to_the_other_mode()
    {
        AccountMode.Other(AccountMode.Seller).Should().Be(AccountMode.Bidder);
        AccountMode.Other(AccountMode.Bidder).Should().Be(AccountMode.Seller);
    }

    [Theory]
    [InlineData(null, true, "not sent")]
    [InlineData("None", true, "not sent")]
    [InlineData("Applied", false, "waiting for an admin")]
    [InlineData("Verified", false, "approved")]
    [InlineData("Rejected", true, "not approved")]
    [InlineData("Revoked", false, "turned off")]
    public void Application_status(string? status, bool canApply, string words)
    {
        SellerApplicationView.CanApply(status).Should().Be(canApply);
        SellerApplicationView.Words(status).Should().Be(words);
    }
}
