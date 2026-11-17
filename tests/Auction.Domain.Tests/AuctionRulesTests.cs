using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

public class AuctionRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private static AuctionInput Good() => new(
        StartUtc: Now.AddMinutes(10),
        EndUtc: Now.AddMinutes(10).AddHours(1),
        StartingPrice: 25m,
        ReservePrice: null);

    private static FieldIssue SingleIssue(AuctionInput input)
    {
        var act = () => AuctionRules.Validate(input, Now);
        var ex = act.Should().Throw<DomainException>().Which;
        ex.Code.Should().Be(ErrorCodes.ValidationFailed);
        return ex.Fields.Should().ContainSingle().Which;
    }

    // ---------- create ----------

    [Fact]
    public void Defaults_are_applied()
    {
        var settings = AuctionRules.Validate(Good(), Now);

        settings.SnipeWindowSec.Should().Be(60);
        settings.ExtensionSec.Should().Be(60);
        settings.MaxTotalExtensionSec.Should().Be(3600);
        settings.IncrementTableId.Should().Be(1);
        settings.FeeScheduleId.Should().Be(1);
        settings.Currency.Should().Be("USD");
    }

    [Fact]
    public void Given_values_win_over_defaults()
    {
        var settings = AuctionRules.Validate(Good() with { SnipeWindowSec = 120, ExtensionSec = 30, MaxTotalExtensionSec = 600, Currency = "eur" }, Now);

        settings.SnipeWindowSec.Should().Be(120);
        settings.ExtensionSec.Should().Be(30);
        settings.MaxTotalExtensionSec.Should().Be(600);
        settings.Currency.Should().Be("EUR");
    }

    [Fact]
    public void Start_too_soon()
    {
        SingleIssue(Good() with { StartUtc = Now.AddSeconds(59), EndUtc = Now.AddHours(1) }).Field.Should().Be("start_time_ms");
    }

    [Fact]
    public void Start_exactly_one_minute_ahead_is_ok()
    {
        var act = () => AuctionRules.Validate(Good() with { StartUtc = Now.AddMinutes(1), EndUtc = Now.AddMinutes(6) }, Now);

        act.Should().NotThrow();
    }

    [Fact]
    public void Shorter_than_5_minutes()
    {
        var start = Now.AddMinutes(10);
        SingleIssue(Good() with { StartUtc = start, EndUtc = start.AddMinutes(4).AddSeconds(59) }).Field.Should().Be("end_time_ms");
    }

    [Fact]
    public void Exactly_5_minutes_is_ok()
    {
        var start = Now.AddMinutes(10);
        var act = () => AuctionRules.Validate(Good() with { StartUtc = start, EndUtc = start.AddMinutes(5) }, Now);

        act.Should().NotThrow();
    }

    [Fact]
    public void Longer_than_14_days()
    {
        var start = Now.AddMinutes(10);
        SingleIssue(Good() with { StartUtc = start, EndUtc = start.AddDays(14).AddSeconds(1) }).Field.Should().Be("end_time_ms");
    }

    [Fact]
    public void End_before_start()
    {
        var start = Now.AddMinutes(10);
        SingleIssue(Good() with { StartUtc = start, EndUtc = start.AddMinutes(-1) }).Field.Should().Be("end_time_ms");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Starting_price_must_be_above_zero(double price)
    {
        SingleIssue(Good() with { StartingPrice = (decimal)price }).Field.Should().Be("starting_price");
    }

    [Fact]
    public void Reserve_below_starting_price()
    {
        SingleIssue(Good() with { ReservePrice = 24.99m }).Field.Should().Be("reserve_price");
    }

    [Fact]
    public void Reserve_equal_to_starting_price_is_ok()
    {
        AuctionRules.Validate(Good() with { ReservePrice = 25m }, Now).ReservePrice.Should().Be(25m);
    }

    [Fact]
    public void Prices_round_to_4_decimals()
    {
        AuctionRules.Validate(Good() with { StartingPrice = 10.123456m }, Now).StartingPrice.Should().Be(10.1235m);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("US1")]
    [InlineData("DOLLARS")]
    public void Bad_currency(string currency)
    {
        SingleIssue(Good() with { Currency = currency }).Field.Should().Be("currency");
    }

    [Fact]
    public void Snipe_window_out_of_range()
    {
        SingleIssue(Good() with { SnipeWindowSec = 601 }).Field.Should().Be("snipe_window_sec");
    }

    [Fact]
    public void One_issue_per_bad_field()
    {
        var act = () => AuctionRules.Validate(Good() with { StartUtc = Now, EndUtc = Now, StartingPrice = 0 }, Now);

        act.Should().Throw<DomainException>().Which.Fields.Select(f => f.Field)
            .Should().Equal("start_time_ms", "end_time_ms", "starting_price");
    }

    // ---------- item status ----------

    [Theory]
    [InlineData(ItemStatus.Draft)]
    [InlineData(ItemStatus.Unsold)]
    public void Draft_and_unsold_items_can_be_listed(ItemStatus status)
    {
        var act = () => AuctionRules.EnsureItemCanBeListed(status);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(ItemStatus.Published)]
    [InlineData(ItemStatus.InAuction)]
    [InlineData(ItemStatus.Sold)]
    [InlineData(ItemStatus.Removed)]
    public void Other_items_cannot_be_listed(ItemStatus status)
    {
        var act = () => AuctionRules.EnsureItemCanBeListed(status);
        act.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.InvalidState);
    }
}
