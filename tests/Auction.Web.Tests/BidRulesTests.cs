using Auction.Contracts;
using Auction.Web.Services.Bidding;
using Auction.Web.Services.Live;
using Auction.Web.Services.Ui;
using FluentAssertions;
using MudBlazor;
using Xunit;
using static Auction.Web.Tests.LiveTestData;

namespace Auction.Web.Tests;

/// <summary>Spec 06 tasks 5, 6 and 10: bid panel rules and the snackbar text per code (R3.3 to R3.12, R8.1).</summary>
public class BidRulesTests
{
    private static readonly IReadOnlyList<IncrementTier> Tiers =
    [
        new() { From = Usd(0m), Increment = Usd(1m) },
        new() { From = Usd(50m), Increment = Usd(2.5m) },
    ];

    internal static LiveFeedUpdate LiveUpdate(string status = "Live", LiveFeedStatus feed = LiveFeedStatus.Live, decimal price = 10m,
        decimal minNext = 11m, int bids = 1, Guid? leader = null) =>
        new(LiveAuctionView.Empty(AuctionId) with
        {
            HasSnapshot = true, Status = status, Currency = "USD", Price = price, MinNextBid = minNext, BidCount = bids, LeaderId = leader,
        }, feed);

    [Theory]
    [InlineData(false, false, "Live", BidPanelMode.SignIn)]
    [InlineData(true, true, "Live", BidPanelMode.OwnListing)]
    [InlineData(true, false, "Live", BidPanelMode.Form)]
    [InlineData(true, false, "Scheduled", BidPanelMode.NotStarted)]
    [InlineData(true, false, "Sealed", BidPanelMode.Closing)]
    [InlineData(false, false, "Settled", BidPanelMode.Settled)]
    [InlineData(true, false, "Cancelled", BidPanelMode.Cancelled)]
    [InlineData(true, false, "Frozen", BidPanelMode.Paused)]
    public void Panel_mode_follows_status_and_viewer(bool signedIn, bool seller, string status, BidPanelMode expected) =>
        BidRules.Mode(signedIn, seller, status).Should().Be(expected);

    [Fact]
    public void Controls_are_on_only_when_live_caught_up_and_not_sending()
    {
        BidRules.ControlsEnabled(LiveUpdate(), sending: false).Should().BeTrue();
        BidRules.ControlsEnabled(LiveUpdate(), sending: true).Should().BeFalse("a bid is on its way");
        BidRules.ControlsEnabled(LiveUpdate(feed: LiveFeedStatus.Reconnecting), false).Should().BeFalse("the feed is reconnecting");
        BidRules.ControlsEnabled(LiveUpdate(feed: LiveFeedStatus.Connecting), false).Should().BeFalse("still catching up");
        BidRules.ControlsEnabled(LiveUpdate(status: "Sealed"), false).Should().BeFalse("not Live");
    }

    [Fact]
    public void Quick_bids_are_min_then_one_and_three_increments_more()
    {
        BidRules.QuickBids(11m, Tiers).Should().Equal(11m, 12m, 14m);
        BidRules.QuickBids(50m, Tiers).Should().Equal(50m, 52.5m, 57.5m);
    }

    [Fact]
    public void Quick_bids_without_a_table_give_just_the_minimum() =>
        BidRules.QuickBids(11m, []).Should().Equal(11m);

    [Theory]
    [InlineData(31, 10, 5, 2, true)]
    [InlineData(30, 10, 5, 2, false)]
    [InlineData(16, 0, 5, 0, true)]    // no bids: 3 x starting price
    [InlineData(15, 0, 5, 0, false)]
    public void Confirm_when_more_than_3_times_the_price(decimal max, decimal price, decimal start, int bids, bool ask) =>
        BidRules.NeedsConfirm(max, price, start, bids).Should().Be(ask);

    [Fact]
    public void Outbid_by_others_only_when_I_led_and_someone_else_leads_now()
    {
        BidRules.OutbidByOthers(Alice, Alice, Bob).Should().BeTrue();
        BidRules.OutbidByOthers(Alice, Bob, Alice).Should().BeFalse("I took the lead");
        BidRules.OutbidByOthers(Alice, Bob, Bob).Should().BeFalse("I was not leading");
        BidRules.OutbidByOthers(null, Alice, Bob).Should().BeFalse("signed out");
    }

    [Theory]
    [InlineData(PlaceBidResponse.Types.Result.Leading, "You're leading at $12.00.", Severity.Success)]
    [InlineData(PlaceBidResponse.Types.Result.OutbidByProxy, "Someone's max is higher. Price now $12.00.", Severity.Warning)]
    [InlineData(PlaceBidResponse.Types.Result.MaxRaised, "Your max is raised. You're still leading.", Severity.Success)]
    public void Accepted_messages(PlaceBidResponse.Types.Result result, string text, Severity severity)
    {
        var message = BidMessages.ForAccepted(new PlaceBidResponse { Result = result, CurrentPrice = Usd(12m) }, "USD");

        message.Text.Should().Be(text);
        message.Severity.Should().Be(severity);
    }

    [Fact]
    public void Extended_is_mentioned() =>
        BidMessages.ForAccepted(new PlaceBidResponse { Result = PlaceBidResponse.Types.Result.Leading, CurrentPrice = Usd(12m), Extended = true }, "USD")
            .Text.Should().EndWith("The auction was extended.");

    [Theory]
    [InlineData("BELOW_MINIMUM", "Your bid is below the minimum")]
    [InlineData("NOT_LIVE", "not open for bids")]
    [InlineData("ENDED", "has ended")]
    [InlineData("AUCTION_FROZEN", "paused")]
    [InlineData("OWN_LOT", "your own listing")]
    [InlineData("RATE_LIMITED", "Too many bids")]
    [InlineData("ACCOUNT_SUSPENDED", "suspended")]
    public void Error_messages_per_code_carry_the_ref(string code, string part)
    {
        var message = BidMessages.ForError(new UiError(code, "server text", "ref-42", new Dictionary<string, string>(), false));

        message.Text.Should().Contain(part).And.EndWith("(Ref: ref-42)");
        message.Severity.Should().Be(Severity.Error);
    }

    [Fact]
    public void Unknown_codes_use_the_server_message() =>
        BidMessages.ForError(new UiError("WHATEVER", "Server says no.", null, new Dictionary<string, string>(), false))
            .Text.Should().Be("Server says no.");

    [Fact]
    public void Outbid_by_others_message_names_the_new_minimum() =>
        BidMessages.OutbidByOthers(52.5m, "USD").Should().Contain("$52.50");
}
