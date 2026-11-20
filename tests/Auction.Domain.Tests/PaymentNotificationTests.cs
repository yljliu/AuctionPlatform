using Auction.Domain.Enums;
using Auction.Infrastructure.Notify;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 10 task 6: notifications for payments, defaults, strikes and second-chance offers (R4.4, R5.6, R6.3 to R6.5).</summary>
public class PaymentNotificationTests
{
    private static readonly Guid Auction = Guid.Parse("a0000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Seller = Guid.Parse("55555555-0000-0000-0000-0000000000b1");
    private static readonly Guid Buyer = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b1");
    private static readonly Guid Next = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000b1");
    private static readonly Guid Sid = Guid.Parse("50000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Offer = Guid.Parse("0ffe0000-0000-0000-0000-0000000000b1");
    private static readonly AuctionInfo Info = new(Auction, "Omega 1962", Seller, "USD");
    private static readonly Recipients Nobody = new(new HashSet<Guid>(), new HashSet<Guid>());

    private static IReadOnlyList<NotificationDraft> Plan(string topic, string payload) =>
        NotificationPlanner.Plan(NotificationPlanner.FromOutbox(topic, payload)!, Info, Nobody);

    [Fact]
    public void Paid_tells_the_buyer_and_the_seller()
    {
        var plan = Plan("PaymentReceived", $$"""{"settlementId":"{{Sid}}","auctionId":"{{Auction}}","buyerId":"{{Buyer}}","sellerId":"{{Seller}}","buyerHandle":"b***b","amount":1312.5}""");

        plan.Select(p => (p.UserId, p.Type, p.EventKey)).Should().BeEquivalentTo(new[]
        {
            (Buyer, NotificationType.PaymentReceived, $"paid:{Sid}"),
            (Seller, NotificationType.BuyerPaid, $"paid-seller:{Sid}"),
        });
        plan[0].Title.Should().Be("Payment received for Omega 1962");
        plan[0].Link.Should().Be($"/me/purchases/{Sid}/receipt");
        plan[1].Title.Should().Be("b***b paid for Omega 1962");
        plan[1].Body.Should().Be("$1,312.50 received.");
    }

    [Fact]
    public void A_first_default_is_a_strike_and_offers_a_second_chance_to_the_seller()
    {
        var plan = Plan("PaymentDefaulted", $$"""{"settlementId":"{{Sid}}","auctionId":"{{Auction}}","buyerId":"{{Buyer}}","sellerId":"{{Seller}}","activeStrikes":1,"suspended":false,"canOfferSecondChance":true}""");

        plan.Should().HaveCount(2);
        plan[0].Should().Match<NotificationDraft>(d => d.UserId == Seller && d.Type == NotificationType.BuyerDefaulted
            && d.Title == "The buyer did not pay for Omega 1962" && d.Body!.Contains("second-chance"));
        plan[1].Should().Match<NotificationDraft>(d => d.UserId == Buyer && d.Type == NotificationType.Strike
            && d.Title == "You got a strike for not paying for Omega 1962 (1 of 3)");
    }

    [Fact]
    public void The_third_default_says_suspended_and_links_to_the_appeal()
    {
        var plan = Plan("PaymentDefaulted", $$"""{"settlementId":"{{Sid}}","auctionId":"{{Auction}}","buyerId":"{{Buyer}}","sellerId":"{{Seller}}","activeStrikes":3,"suspended":true,"canOfferSecondChance":false}""");

        plan[0].Body.Should().BeNull("no second chance to offer");
        plan[1].Should().Match<NotificationDraft>(d => d.Type == NotificationType.AccountSuspended
            && d.Title == "Your account is suspended: 3 unpaid wins" && d.Link == "/account/appeal");
    }

    [Theory]
    [InlineData("SecondChanceOffered", NotificationType.SecondChance, "Second chance: buy Omega 1962 for $900")]
    [InlineData("SecondChanceAccepted", NotificationType.SecondChanceAnswer, "The bidder accepted your offer for Omega 1962")]
    [InlineData("SecondChanceDeclined", NotificationType.SecondChanceAnswer, "The bidder declined your offer for Omega 1962")]
    [InlineData("SecondChanceExpired", NotificationType.SecondChanceAnswer, "Your offer for Omega 1962 expired")]
    public void Second_chance_goes_to_the_bidder_and_answers_go_to_the_seller(string topic, NotificationType type, string title)
    {
        var plan = Plan(topic, $$"""{"offerId":"{{Offer}}","auctionId":"{{Auction}}","sellerId":"{{Seller}}","bidderId":"{{Next}}","amount":900}""");

        var only = plan.Should().ContainSingle().Subject;
        only.Type.Should().Be(type);
        only.Title.Should().Be(title);
        only.UserId.Should().Be(type == NotificationType.SecondChance ? Next : Seller);
    }

    [Fact]
    public void The_same_message_twice_gives_the_same_keys()
    {
        var payload = $$"""{"offerId":"{{Offer}}","auctionId":"{{Auction}}","sellerId":"{{Seller}}","bidderId":"{{Next}}","amount":900}""";

        Plan("SecondChanceOffered", payload).Select(p => p.EventKey)
            .Should().Equal(Plan("SecondChanceOffered", payload).Select(p => p.EventKey));
    }

    [Fact]
    public void A_message_missing_ids_is_ignored() =>
        NotificationPlanner.FromOutbox("PaymentReceived", $$"""{"auctionId":"{{Auction}}"}""").Should().BeNull();
}
