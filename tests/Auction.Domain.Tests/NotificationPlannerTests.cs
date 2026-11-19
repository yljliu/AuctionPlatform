using Auction.Domain.Enums;
using Auction.Infrastructure.Notify;
using Auction.Infrastructure.Redis;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 07 task 3 without SQL or Redis: who gets which notification, with which key (R2, R2.1, R2.2).</summary>
public class NotificationPlannerTests
{
    private static readonly Guid Auction = Guid.Parse("a0000000-0000-0000-0000-0000000000a7");
    private static readonly Guid Seller = Guid.Parse("55555555-0000-0000-0000-000000000007");
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Carol = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static readonly AuctionInfo Info = new(Auction, "Omega 1962", Seller, "USD");

    private static Recipients Who(Guid[]? watchers = null, Guid[]? bidders = null) =>
        new((watchers ?? []).ToHashSet(), (bidders ?? []).ToHashSet());

    [Fact]
    public void Outbid_tells_only_the_previous_leader()
    {
        var plan = NotificationPlanner.Plan(new OutbidEvent(Auction, 7, NewLeader: Bob, OutbidUser: Alice, PriceC: 125_000), Info, Who());

        plan.Should().ContainSingle();
        plan[0].Should().Be(new NotificationDraft(Alice, NotificationType.Outbid, $"outbid:{Auction}:7",
            "You've been outbid on Omega 1962", "The price is now $1,250.", $"/a/{Auction}", Auction));
    }

    [Fact]
    public void Nobody_is_told_they_outbid_themselves() =>
        NotificationPlanner.Plan(new OutbidEvent(Auction, 7, Alice, Alice, 100), Info, Who()).Should().BeEmpty();

    [Fact]
    public void Closing_soon_goes_to_watchers_and_bidders_once_each_with_one_key_per_auction()
    {
        var plan = NotificationPlanner.Plan(new ClosingSoonEvent(Auction), Info, Who(watchers: [Alice, Carol], bidders: [Alice, Bob, Seller]));

        plan.Select(p => p.UserId).Should().BeEquivalentTo([Alice, Carol, Bob], "the seller and repeats are left out");
        plan.Should().OnlyContain(p => p.EventKey == $"soon:{Auction}" && p.Type == NotificationType.EndingSoon && p.Title == "Omega 1962 ends in 1 minute");
    }

    [Fact]
    public void Sold_gives_won_lost_and_sold()
    {
        var plan = NotificationPlanner.Plan(new SettledEvent(Auction, "Sold", 1250m, Bob, Seller), Info, Who(bidders: [Alice, Bob, Carol]));

        plan.Should().ContainSingle(p => p.UserId == Bob && p.Type == NotificationType.Won && p.Title == "You won Omega 1962 for $1,250" && p.Link == "/me/purchases");
        plan.Where(p => p.Type == NotificationType.Lost).Select(p => p.UserId).Should().BeEquivalentTo([Alice, Carol]);
        plan.Where(p => p.Type == NotificationType.Lost).Should().OnlyContain(p => p.Title == "Omega 1962 sold for $1,250");
        plan.Should().ContainSingle(p => p.UserId == Seller && p.Type == NotificationType.AuctionSold && p.Title == "Your item sold for $1,250" && p.Link == "/sell/sales");
        plan.Select(p => (p.UserId, p.EventKey)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Unsold_tells_the_seller_and_the_bidders()
    {
        var plan = NotificationPlanner.Plan(new SettledEvent(Auction, "Unsold", 0, null, Seller), Info, Who(bidders: [Alice]));

        plan.Should().ContainSingle(p => p.UserId == Seller && p.Type == NotificationType.AuctionUnsold && p.Title == "Your item did not sell");
        plan.Should().ContainSingle(p => p.UserId == Alice && p.Type == NotificationType.Lost);
    }

    [Fact]
    public void Cancelled_goes_to_bidders_and_watchers_but_not_the_seller()
    {
        var plan = NotificationPlanner.Plan(new CancelledEvent(Auction, null), Info, Who(watchers: [Carol, Seller], bidders: [Alice]));

        plan.Select(p => p.UserId).Should().BeEquivalentTo([Carol, Alice]);
        plan.Should().OnlyContain(p => p.EventKey == $"cancelled:{Auction}" && p.Title == "Omega 1962 was cancelled");
    }

    [Theory]
    [InlineData(1250, "$1,250")]
    [InlineData(1250.5, "$1,250.50")]
    [InlineData(0.99, "$0.99")]
    public void Money_in_titles(decimal amount, string text) => NotificationPlanner.Money(amount, "USD").Should().Be(text);

    [Fact]
    public void Bid_entries_with_an_outbid_user_become_outbid_events()
    {
        var e = NotificationPlanner.FromStream(new Dictionary<string, string>
        {
            ["type"] = "Bid", ["auction"] = Auction.ToString(), ["seq"] = "9", ["user"] = Bob.ToString(), ["outbid"] = Alice.ToString(), ["priceC"] = "5000",
        });

        e.Should().Be(new OutbidEvent(Auction, 9, Bob, Alice, 5000));
    }

    [Fact]
    public void Bid_entries_without_an_outbid_user_are_ignored() =>
        NotificationPlanner.FromStream(new Dictionary<string, string>
        {
            ["type"] = "Bid", ["auction"] = Auction.ToString(), ["seq"] = "9", ["user"] = Bob.ToString(), ["outbid"] = "",
        }).Should().BeNull();

    [Theory]
    [InlineData("ClosingSoon", typeof(ClosingSoonEvent))]
    [InlineData("Cancelled", typeof(CancelledEvent))]
    [InlineData("Sealed", null)]
    public void State_entries_by_change(string change, Type? expected)
    {
        var e = NotificationPlanner.FromStream(new Dictionary<string, string> { ["type"] = "State", ["auction"] = Auction.ToString(), ["change"] = change });

        (e?.GetType()).Should().Be(expected);
    }

    [Fact]
    public void Settled_outbox_rows_are_read()
    {
        var e = NotificationPlanner.FromOutbox("AuctionSettled",
            $"{{\"auctionId\":\"{Auction}\",\"outcome\":\"Sold\",\"price\":1250.0,\"winnerId\":\"{Bob}\",\"sellerId\":\"{Seller}\",\"settlementId\":\"{Guid.NewGuid()}\"}}");

        e.Should().Be(new SettledEvent(Auction, "Sold", 1250m, Bob, Seller));
    }

    [Fact]
    public void Cancelled_outbox_rows_use_the_same_event_as_the_stream() =>
        NotificationPlanner.FromOutbox("AuctionCancelled", $"{{\"auctionId\":\"{Auction}\",\"reason\":\"x\"}}").Should().Be(new CancelledEvent(Auction, null));

    [Theory]
    [InlineData("AuctionCreated", "{\"auctionId\":\"a0000000-0000-0000-0000-0000000000a7\"}")]
    [InlineData("AuctionSettled", "not json")]
    [InlineData("AuctionSettled", "{}")]
    public void Other_or_broken_outbox_rows_are_ignored(string topic, string payload) =>
        NotificationPlanner.FromOutbox(topic, payload).Should().BeNull();

    [Fact]
    public void Only_events_that_need_people_load_watchers_and_bidders()
    {
        NotificationPlanner.NeedsRecipients(new OutbidEvent(Auction, 1, Bob, Alice, 1)).Should().BeFalse();
        NotificationPlanner.NeedsRecipients(new ClosingSoonEvent(Auction)).Should().BeTrue();
    }

    [Fact]
    public void Pushed_message_round_trips_and_finds_the_auction_in_the_link()
    {
        var message = new NotificationMessage(5, Alice, NotificationType.Outbid, "t", null, $"/a/{Auction}", Auction, false, 100);

        NotificationMessage.FromJson(message.ToJson()).Should().Be(message);
        NotificationMessage.AuctionOf($"/a/{Auction}").Should().Be(Auction);
        NotificationMessage.AuctionOf("/me/purchases").Should().BeNull();
        NotificationStore.Channel(Alice).Should().Be($"u:{Alice}");
    }

    [Fact]
    public void Watcher_script_and_load_count_are_wired()
    {
        LuaScripts.Load("watchers.lua").Should().Contain("HINCRBY").And.Contain("'watchers'");
        LuaScripts.Load("load.lua").Should().Contain("'watchers', tonumber(ARGV[12])");
    }
}
