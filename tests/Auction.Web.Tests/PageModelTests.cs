using Auction.Contracts;
using Auction.Web.Services.Catalog;
using Auction.Web.Services.Live;
using Auction.Web.Services.Seller;
using Auction.Web.Services.Ui;
using FluentAssertions;
using Xunit;
using static Auction.Web.Tests.LiveTestData;

namespace Auction.Web.Tests;

/// <summary>Spec 06 tasks 3, 8 and 10: browse URL state, listing forms, money and time text, settled outcome.</summary>
public class PageModelTests
{
    // ---------- browse (R2.2) ----------

    [Fact]
    public void Browse_query_reads_the_url_and_drops_bad_values()
    {
        var q = BrowseQuery.Parse("lamp", "Settled", "10", "x", "3600", "New,Bogus,Good", "price_asc", "2");

        q.Q.Should().Be("lamp");
        q.Status.Should().Be("Settled");
        q.Min.Should().Be(10m);
        q.Max.Should().BeNull("x is not a number");
        q.Ending.Should().Be(3600);
        q.Conditions.Should().Equal("New", "Good");
        q.Page.Should().Be(2);
        q.IsSearch.Should().BeTrue();
        q.ToSort().Should().Be(AuctionSort.PriceAsc);
    }

    [Fact]
    public void Browse_query_round_trips_through_the_url()
    {
        var q = new BrowseQuery("lamp", null, 5m, 50m, null, ["LikeNew"], "bids", 3);
        var url = q.ToQuery();

        var back = BrowseQuery.Parse((string?)url["q"], (string?)url["status"], (string?)url["min"], (string?)url["max"],
            (string?)url["ending"], (string?)url["cond"], (string?)url["sort"], (string?)url["page"]);

        back.Q.Should().Be("lamp");
        back.Min.Should().Be(5m);
        back.Max.Should().Be(50m);
        back.Conditions.Should().Equal("LikeNew");
        back.Sort.Should().Be("bids");
        back.Page.Should().Be(3);
        url["status"].Should().BeNull("defaults stay out of the URL");
    }

    [Fact]
    public void Browse_filter_carries_category_prices_and_conditions()
    {
        var filter = new BrowseQuery(Min: 5m, Ending: 3600, Conditions: ["New"]).ToFilter(categoryId: 7);

        filter.CategoryId.Should().Be(7);
        filter.MinPrice.ToDomain().Amount.Should().Be(5m);
        filter.MaxPrice.Should().BeNull();
        filter.EndingWithinSec.Should().Be(3600);
        filter.Conditions.Should().Equal("New");
        filter.Status.Should().BeEmpty("empty means Live");
    }

    [Fact]
    public void One_letter_is_not_a_search() =>
        new BrowseQuery(Q: "a").IsSearch.Should().BeFalse();

    [Fact]
    public void Category_slugs_are_found_in_children()
    {
        var roots = new List<Category> { new() { CategoryId = 1, Slug = "home", Name = "Home", Children = { new Category { CategoryId = 2, Slug = "lamps", Name = "Lamps" } } } };

        CategoryCache.FindBySlug(roots, "lamps")!.CategoryId.Should().Be(2);
        CategoryCache.FindBySlug(roots, "nope").Should().BeNull();
        CategoryCache.Flatten(roots).Select(x => x.Depth).Should().Equal(0, 1);
    }

    // ---------- listing forms (R5.1, R5.2) ----------

    [Fact]
    public void Item_draft_checks_match_the_server_rules()
    {
        var draft = new ItemDraft { ShippingType = "BuyerPays", ReturnsAccepted = true, HandlingDays = 40 };

        draft.Validate().Keys.Should().BeEquivalentTo("category_id", "title", "description", "shipping_cost", "handling_days", "return_window_days");
    }

    [Fact]
    public void A_good_item_draft_passes_and_drops_unused_fields()
    {
        var draft = new ItemDraft
        {
            CategoryId = 3, Title = " Lamp ", Description = "Old brass lamp", ShippingType = "FreeShipping", ShippingCost = 9m,
            WarrantyType = "None", WarrantyNote = "ignored",
        };

        draft.Validate().Should().BeEmpty();
        var fields = draft.ToFields();
        fields.Title.Should().Be("Lamp");
        fields.ShippingCost.Should().BeNull("free shipping has no cost");
        fields.WarrantyNote.Should().BeEmpty();
        fields.ReturnWindowDays.Should().BeNull();
    }

    [Fact]
    public void Local_start_time_becomes_utc()
    {
        var draft = new AuctionDraft { StartDate = new DateTime(2026, 11, 20), StartTime = new TimeSpan(9, 30, 0), Duration = TimeSpan.FromHours(1) };

        // UTC-8 (Pacific): 09:30 local is 17:30 UTC.
        draft.StartUtc(-480).Should().Be(new DateTimeOffset(2026, 11, 20, 17, 30, 0, TimeSpan.Zero));
        var fields = draft.ToFields(-480);
        (fields.EndTimeMs - fields.StartTimeMs).Should().Be(3_600_000);
    }

    [Fact]
    public void Auction_draft_checks_start_and_prices()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var draft = new AuctionDraft { StartDate = now.Date, StartTime = new TimeSpan(12, 0, 30), StartingPrice = 10m, ReservePrice = 5m };

        draft.Validate(now, 0).Keys.Should().BeEquivalentTo("start_time_ms", "reserve_price");
    }

    [Fact]
    public void Default_start_is_about_10_minutes_ahead_on_a_5_minute_mark()
    {
        var draft = AuctionDraft.Default(new DateTimeOffset(2026, 11, 20, 12, 3, 0, TimeSpan.Zero), 0);

        draft.StartTime.Should().Be(new TimeSpan(12, 15, 0));
        draft.Validate(new DateTimeOffset(2026, 11, 20, 12, 3, 0, TimeSpan.Zero), 0).Should().BeEmpty();
    }

    [Theory]
    [InlineData(100, null, 92)]       // starting price 100, fee 8 %
    [InlineData(100, 250, 230)]       // the reserve wins
    public void Estimated_payout(decimal start, int? reserve, decimal payout) =>
        new AuctionDraft { StartingPrice = start, ReservePrice = reserve }.EstimatedPayout(8m).Should().Be(payout);

    // ---------- text ----------

    [Theory]
    [InlineData(1234.5, "USD", "$1,234.50")]
    [InlineData(10, "EUR", "10.00 EUR")]
    public void Money_text(decimal amount, string currency, string text) => UiFormat.Money(amount, currency).Should().Be(text);

    [Theory]
    [InlineData("LikeNew", "Like new")]
    [InlineData("ForParts", "For parts")]
    [InlineData("Live", "Live")]
    public void Enum_names_become_words(string name, string words) => UiFormat.Words(name).Should().Be(words);

    [Fact]
    public void Time_left_text()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);

        UiFormat.TimeLeft(1_000_000 + (2 * 86_400_000) + (3 * 3_600_000), now).Should().Be("2d 3h");
        UiFormat.TimeLeft(1_000_000 + 310_000, now).Should().Be("5m 10s");
        UiFormat.TimeLeft(999_000, now).Should().Be("Ended");
    }

    [Fact]
    public void Ui_error_text_has_the_ref() =>
        new UiError("X", "Nope.", "abc", new Dictionary<string, string>(), false).Text.Should().Be("Nope. (Ref: abc)");

    // ---------- settled outcome (R3.10) ----------

    [Fact]
    public void Settled_event_sets_outcome_winner_and_change()
    {
        var view = LiveAuctionView.Empty(AuctionId).Apply(Snapshot(3, price: 40m, bidCount: 2));
        var settled = new AuctionEvent
        {
            Seq = 4, AuctionId = AuctionId.ToString(),
            StateChanged = new StateChanged { Status = "Settled", Change = "Settled", Outcome = "Sold", FinalPrice = Usd(40m), Winner = new PublicUser { Handle = "b***b" } },
        };

        var after = view.Apply(settled);

        after.Status.Should().Be("Settled");
        after.OutcomeOrGuess.Should().Be("Sold");
        after.WinnerHandle.Should().Be("b***b");
        after.LastChange.Should().Be("Settled");
    }

    [Fact]
    public void Settled_without_the_event_guesses_by_the_settle_rule()
    {
        var view = LiveAuctionView.Empty(AuctionId) with { HasSnapshot = true, Status = "Settled", BidCount = 0 };

        view.OutcomeOrGuess.Should().Be("Unsold");
        (view with { BidCount = 2, ReserveMet = false }).OutcomeOrGuess.Should().Be("Unsold");
        (view with { BidCount = 2, ReserveMet = true }).OutcomeOrGuess.Should().Be("Sold");
        (view with { Status = "Live" }).OutcomeOrGuess.Should().BeNull();
    }
}
