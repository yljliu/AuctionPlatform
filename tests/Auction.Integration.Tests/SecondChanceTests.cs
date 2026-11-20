using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Account;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Payments;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>Spec 10 task 4: second-chance offers after a default (design W12).</summary>
[Collection(SqlServerCollection.Name)]
public class SecondChanceTests(SqlServerFixture sql)
{
    private static SecondChanceManager Offers(LifecycleTestDb t, TimeProvider? clock = null)
    {
        var db = t.NewContext();
        return new SecondChanceManager(db, new SettlementQueries(db), new OutboxWriter(db, clock ?? TimeProvider.System), clock ?? TimeProvider.System);
    }

    private static async Task<Guid> UserAsync(LifecycleTestDb t, string name, UserStatus status = UserStatus.Active)
    {
        var now = DateTime.UtcNow;
        var user = new User { UserId = Guid.NewGuid(), Username = name, Email = $"{name}@t.local", PasswordHash = "x", Status = status, CreatedAt = now, UpdatedAt = now };
        t.Db.Users.Add(user);
        await t.Db.SaveChangesAsync();
        return user.UserId;
    }

    /// <summary>A Defaulted sale to bob; other bidders get bid rows at their amounts.</summary>
    private static async Task<(Guid Settlement, Guid Auction)> DefaultedAsync(LifecycleTestDb t, Guid buyer, params (Guid User, decimal Amount)[] others)
    {
        var a = await t.AddAuctionAsync(new(AuctionStatus.Settled, CurrentPrice: 100m, Leader: buyer, BidCount: 1 + others.Length,
            Shipping: ShippingType.BuyerPays, ShippingCost: 10m));
        var now = DateTime.UtcNow;
        var eseq = 1;
        foreach (var (user, amount) in others.Append((buyer, 100m)))
        {
            t.Db.Bids.Add(new Bid { AuctionId = a.AuctionId, UserId = user, ESeq = eseq, Idx = 0, Seq = eseq, Kind = BidKind.Manual, Amount = amount, AcceptedAt = now });
            eseq++;
        }

        var s = new Settlement
        {
            SettlementId = Guid.NewGuid(), AuctionId = a.AuctionId, BuyerId = buyer, SellerId = LifecycleTestDb.Seller, Currency = "USD",
            HammerPrice = 100m, TotalDue = 115m, Status = SettlementStatus.Defaulted, PaymentDeadline = now.AddMinutes(-5),
            CreatedAt = now.AddDays(-3), UpdatedAt = now,
        };
        t.Db.Settlements.Add(s);
        await t.Db.SaveChangesAsync();
        await t.Db.Auctions.Where(x => x.AuctionId == a.AuctionId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Outcome, AuctionOutcome.Defaulted));
        return (s.SettlementId, a.AuctionId);
    }

    private static async Task<DomainException> Fails(Func<Task> call) => (await call.Should().ThrowAsync<DomainException>()).Which;

    [Fact]
    public async Task The_offer_goes_to_the_highest_other_bidder_at_their_highest_bid()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sco_next");
        var bob = await UserAsync(t, "bob");
        var alice = await UserAsync(t, "alice");
        var carol = await UserAsync(t, "carol");
        var (sid, _) = await DefaultedAsync(t, bob, (carol, 60m), (alice, 80m), (alice, 90m), (carol, 70m));

        var offer = (await Offers(t).SendAsync(LifecycleTestDb.Seller, sid)).Offer;

        offer.BidderId.Should().Be(alice);
        offer.Amount.Should().Be(90m);
        offer.Status.Should().Be(SecondChanceStatus.Open);
        offer.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(48), TimeSpan.FromMinutes(1));
        await using var db = t.NewContext();
        (await db.Outbox.CountAsync(x => x.Topic == SecondChanceTopics.Offered)).Should().Be(1);
    }

    [Fact]
    public async Task No_other_bidder_or_a_suspended_one_cannot_get_an_offer()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sco_none");
        var bob = await UserAsync(t, "bob");
        var (alone, _) = await DefaultedAsync(t, bob);
        var sus = await UserAsync(t, "sus", UserStatus.Suspended);
        var (onlySuspended, _) = await DefaultedAsync(t, bob, (sus, 90m));

        (await Fails(() => Offers(t).SendAsync(LifecycleTestDb.Seller, alone))).Code.Should().Be(ErrorCodes.InvalidState);
        (await Fails(() => Offers(t).SendAsync(LifecycleTestDb.Seller, onlySuspended))).Code.Should().Be(ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Send_twice_gives_one_offer_and_only_the_seller_may_send()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sco_twice");
        var bob = await UserAsync(t, "bob");
        var alice = await UserAsync(t, "alice");
        var (sid, _) = await DefaultedAsync(t, bob, (alice, 90m));

        (await Fails(() => Offers(t).SendAsync(Guid.NewGuid(), sid))).Code.Should().Be(ErrorCodes.NotOwner);
        var first = await Offers(t).SendAsync(LifecycleTestDb.Seller, sid);
        var second = await Offers(t).SendAsync(LifecycleTestDb.Seller, sid);

        second.Offer.OfferId.Should().Be(first.Offer.OfferId);
        await using var db = t.NewContext();
        (await db.SecondChanceOffers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task After_7_days_it_is_too_late_to_send()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sco_7days");
        var bob = await UserAsync(t, "bob");
        var alice = await UserAsync(t, "alice");
        var (sid, _) = await DefaultedAsync(t, bob, (alice, 90m));
        var later = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(8));

        (await Fails(() => Offers(t, later).SendAsync(LifecycleTestDb.Seller, sid))).Code.Should().Be(ErrorCodes.DeadlinePassed);
    }

    [Fact]
    public async Task Accept_makes_a_new_second_chance_settlement_and_sells_the_auction()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sco_accept");
        var bob = await UserAsync(t, "bob");
        var alice = await UserAsync(t, "alice");
        var (sid, aid) = await DefaultedAsync(t, bob, (alice, 90m));
        var offerId = (await Offers(t).SendAsync(LifecycleTestDb.Seller, sid)).Offer.OfferId;

        (await Fails(() => Offers(t).AcceptAsync(bob, offerId))).Code.Should().Be(ErrorCodes.NotOwner);
        var accepted = (await Offers(t).AcceptAsync(alice, offerId)).Offer;
        var again = (await Offers(t).AcceptAsync(alice, offerId)).Offer;

        accepted.Status.Should().Be(SecondChanceStatus.Accepted);
        again.NewSettlementId.Should().Be(accepted.NewSettlementId);
        await using var db = t.NewContext();
        var s = await db.Settlements.SingleAsync(x => x.SettlementId == accepted.NewSettlementId);
        s.BuyerId.Should().Be(alice);
        s.IsSecondChance.Should().BeTrue();
        s.Status.Should().Be(SettlementStatus.PendingPayment);
        s.HammerPrice.Should().Be(90m);
        s.BuyerPremium.Should().Be(4.5m, "5 % premium from fee schedule 1");
        s.ShippingCost.Should().Be(10m);
        s.PaymentDeadline.Should().BeCloseTo(DateTime.UtcNow.AddHours(72), TimeSpan.FromMinutes(1));
        (await db.Auctions.SingleAsync(x => x.AuctionId == aid)).Outcome.Should().Be(AuctionOutcome.Sold);
        (await db.Settlements.CountAsync(x => x.AuctionId == aid)).Should().Be(2, "the defaulted one stays as history");
    }

    [Fact]
    public async Task Decline_is_final_and_an_expired_offer_cannot_be_accepted()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sco_decline");
        var bob = await UserAsync(t, "bob");
        var alice = await UserAsync(t, "alice");
        var (one, _) = await DefaultedAsync(t, bob, (alice, 90m));
        var (two, _) = await DefaultedAsync(t, bob, (alice, 50m));
        var declineId = (await Offers(t).SendAsync(LifecycleTestDb.Seller, one)).Offer.OfferId;
        var expireId = (await Offers(t).SendAsync(LifecycleTestDb.Seller, two)).Offer.OfferId;

        (await Offers(t).DeclineAsync(alice, declineId)).Offer.Status.Should().Be(SecondChanceStatus.Declined);
        (await Offers(t).AcceptAsync(alice, declineId)).Offer.Status.Should().Be(SecondChanceStatus.Declined, "the first answer stands");

        await t.Db.SecondChanceOffers.Where(o => o.OfferId == expireId).ExecuteUpdateAsync(o => o.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        (await Fails(() => Offers(t).AcceptAsync(alice, expireId))).Code.Should().Be(ErrorCodes.DeadlinePassed);

        var sweeper = new DefaultSweeper(t.Connections, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<DefaultSweeper>.Instance);
        (await sweeper.ExpireOffersAsync(t.Grant)).Should().Be(1);
        await using var db = t.NewContext();
        (await db.SecondChanceOffers.SingleAsync(o => o.OfferId == expireId)).Status.Should().Be(SecondChanceStatus.Expired);
        (await db.Outbox.CountAsync(x => x.Topic == SecondChanceTopics.Expired)).Should().Be(1);
    }

    [Fact]
    public async Task The_sales_list_shows_when_an_offer_is_possible_and_then_its_status()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sco_list");
        var bob = await UserAsync(t, "bob");
        var alice = await UserAsync(t, "alice");
        var (sid, _) = await DefaultedAsync(t, bob, (alice, 90m));
        await using var db = t.NewContext();
        var queries = new SettlementQueries(db);

        (await queries.ByIdAsync(sid))!.CanSecondChance.Should().BeTrue();
        await Offers(t).SendAsync(LifecycleTestDb.Seller, sid);

        var row = (await new SettlementQueries(t.NewContext()).ByIdAsync(sid))!;
        row.CanSecondChance.Should().BeFalse();
        row.OfferStatus.Should().Be((byte)SecondChanceStatus.Open);
    }
}
