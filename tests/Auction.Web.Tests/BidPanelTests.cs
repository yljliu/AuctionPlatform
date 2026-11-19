using Auction.Contracts;
using Auction.Web.Components.Bidding;
using Auction.Web.Services.Bidding;
using Auction.Web.Services.Live;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Xunit;
using static Auction.Web.Tests.BidRulesTests;
using static Auction.Web.Tests.LiveTestData;

namespace Auction.Web.Tests;

/// <summary>
/// Spec 06 task 10 (bUnit): the bid panel's disabled states, the 3x confirm, and the snackbar after a bid.
/// MudBlazor runs for real; JavaScript calls are answered loosely; the Api is a fake.
/// </summary>
public sealed class BidPanelTests : TestContext
{
    private readonly BidSenderTests.FakeBidClient _client = new(BidSenderTests.Leading());

    public BidPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IBidClient>(_client);
        Services.AddSingleton(sp => new BidSender(sp.GetRequiredService<IBidClient>(), TimeProvider.System) { Backoff = _ => TimeSpan.Zero });
    }

    private static readonly IReadOnlyList<IncrementTier> Tiers = [new() { From = Usd(0m), Increment = Usd(1m) }];

    private IRenderedComponent<BidPanel> Render(LiveFeedUpdate update, Guid? me = null, bool seller = false) =>
        RenderComponent<BidPanel>(p => p
            .Add(x => x.AuctionId, AuctionId.ToString())
            .Add(x => x.Update, update)
            .Add(x => x.IncrementTiers, Tiers)
            .Add(x => x.StartingPrice, 5m)
            .Add(x => x.Me, me)
            .Add(x => x.IsSeller, seller));

    private static IEnumerable<AngleSharp.Dom.IElement> QuickButtons(IRenderedComponent<BidPanel> cut) =>
        cut.FindAll("#quick-bids button");

    [Fact]
    public void Live_and_caught_up_turns_the_controls_on()
    {
        var cut = Render(LiveUpdate(), me: Alice);

        QuickButtons(cut).Should().HaveCount(3).And.OnlyContain(b => !b.HasAttribute("disabled"));
        cut.Find("#min-next").TextContent.Should().Be("$11.00");
    }

    [Fact]
    public void Reconnecting_turns_the_controls_off()
    {
        var cut = Render(LiveUpdate(feed: LiveFeedStatus.Reconnecting), me: Alice);

        QuickButtons(cut).Should().OnlyContain(b => b.HasAttribute("disabled"));
        cut.Find("#live-bid-button").HasAttribute("disabled").Should().BeTrue();
    }

    [Theory]
    [InlineData("Sealed", "#bid-closing", "Closing")]
    [InlineData("Scheduled", "#bid-not-started", "not started")]
    [InlineData("Frozen", "#bid-paused", "paused")]
    public void Not_live_replaces_the_form(string status, string selector, string text)
    {
        var cut = Render(LiveUpdate(status: status), me: Alice);

        cut.Find(selector).TextContent.Should().Contain(text);
        QuickButtons(cut).Should().BeEmpty();
    }

    [Fact]
    public void Signed_out_sees_sign_in_that_comes_back_here()
    {
        var cut = Render(LiveUpdate(), me: null);

        cut.Find("#bid-signin").GetAttribute("href").Should().Be($"/account/login?returnUrl=%2Fa%2F{AuctionId}");
    }

    [Fact]
    public void Seller_sees_your_listing()
    {
        var cut = Render(LiveUpdate(), me: Alice, seller: true);

        cut.Find("#bid-own").TextContent.Should().Contain("Your listing");
    }

    [Fact]
    public void Settled_shows_the_winner_and_price()
    {
        var update = LiveUpdate(status: "Settled", price: 40m, bids: 3, leader: Bob) with { };
        update = update with { View = update.View with { Outcome = "Sold", WinnerHandle = "b***b" } };

        var cut = Render(update, me: Alice);

        cut.Find("#bid-settled").TextContent.Should().Contain("Sold to b***b").And.Contain("$40.00");
    }

    [Fact]
    public void Leading_status_shows_my_max()
    {
        var cut = RenderComponent<BidPanel>(p => p
            .Add(x => x.AuctionId, AuctionId.ToString())
            .Add(x => x.Update, LiveUpdate(leader: Alice))
            .Add(x => x.IncrementTiers, Tiers)
            .Add(x => x.Me, Alice)
            .Add(x => x.MyMax, 30m));

        cut.Find("#my-status").TextContent.Should().Contain("You're leading").And.Contain("$30.00");
    }

    [Fact]
    public async Task A_bid_shows_the_leading_snackbar_and_sends_once()
    {
        var cut = Render(LiveUpdate(), me: Alice);

        await cut.InvokeAsync(() => cut.Instance.BidAsync(11m));

        _client.Requests.Should().ContainSingle().Which.MaxAmount.ToDomain().Amount.Should().Be(11m);
        Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(s => s.Message).Should().Contain("You're leading at $12.00.");
    }

    [Fact]
    public async Task More_than_3x_asks_first_and_no_sends_nothing()
    {
        var dialogs = RenderComponent<MudDialogProvider>();
        var cut = Render(LiveUpdate(price: 10m), me: Alice);

        var bid = cut.InvokeAsync(() => cut.Instance.BidAsync(31m));
        dialogs.WaitForAssertion(() => dialogs.Find("#confirm-text").TextContent.Should().Contain("more than 3 times"));
        dialogs.Find("#confirm-no").Click();
        await bid;

        _client.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task More_than_3x_and_yes_sends_the_bid()
    {
        var dialogs = RenderComponent<MudDialogProvider>();
        var cut = Render(LiveUpdate(price: 10m), me: Alice);

        var bid = cut.InvokeAsync(() => cut.Instance.BidAsync(31m));
        dialogs.WaitForAssertion(() => dialogs.Find("#confirm-yes"));
        dialogs.Find("#confirm-yes").Click();
        await bid;

        _client.Requests.Should().ContainSingle();
    }
}
