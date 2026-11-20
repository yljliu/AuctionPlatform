using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Domain.Settlements;
using Auction.Infrastructure.Account;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Auction.Infrastructure.Payments;

public static class SecondChanceTopics
{
    public const string Offered = "SecondChanceOffered";
    public const string Accepted = "SecondChanceAccepted";
    public const string Declined = "SecondChanceDeclined";
    public const string Expired = "SecondChanceExpired";
}

/// <summary>An offer with the auction card of the defaulted sale.</summary>
public sealed record SecondChanceView(SecondChanceOffer Offer, SettlementListRow Defaulted);

/// <summary>
/// Second-chance offers (spec 10 R6, design W12): after a default the seller may offer the item to the next
/// highest active bidder at that bidder's highest bid, for 48 hours. Accepting creates a new settlement.
/// </summary>
public sealed class SecondChanceManager(AuctionDbContext db, SettlementQueries settlements, OutboxWriter outbox, TimeProvider clock)
{
    public static readonly TimeSpan OfferLasts = TimeSpan.FromHours(48);
    public static readonly TimeSpan SendWithin = TimeSpan.FromDays(7);

    private sealed class Next
    {
        public Guid UserId { get; set; }
        public decimal Amount { get; set; }
    }

    public async Task<SecondChanceView> SendAsync(Guid sellerId, Guid defaultedSettlementId, CancellationToken ct = default)
    {
        var s = await db.Settlements.AsNoTracking().FirstOrDefaultAsync(x => x.SettlementId == defaultedSettlementId, ct)
                ?? throw new DomainException(ErrorCodes.NotFound, "Sale not found.");
        if (s.SellerId != sellerId)
        {
            throw new DomainException(ErrorCodes.NotOwner, "This is not your sale.");
        }

        var existing = await db.SecondChanceOffers.AsNoTracking().FirstOrDefaultAsync(o => o.DefaultedSettlementId == s.SettlementId, ct);
        if (existing != null)
        {
            return await ViewAsync(existing, ct);   // one offer per default (R6.6)
        }

        if (s.Status != SettlementStatus.Defaulted)
        {
            throw new DomainException(ErrorCodes.InvalidState, "A second-chance offer needs a sale the buyer did not pay.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (now > s.UpdatedAt + SendWithin)
        {
            throw new DomainException(ErrorCodes.DeadlinePassed, "Second-chance offers must be sent within 7 days of the default.");
        }

        var next = await db.Database.GetDbConnection().QuerySingleOrDefaultAsync<Next>(new CommandDefinition(
            DefaultSweeper.NextBidderSql, new { s.AuctionId, s.BuyerId }, cancellationToken: ct))
            ?? throw new DomainException(ErrorCodes.InvalidState, "No other bidder can take it.");

        var offer = new SecondChanceOffer
        {
            OfferId = Guid.NewGuid(), DefaultedSettlementId = s.SettlementId, AuctionId = s.AuctionId, SellerId = sellerId,
            BidderId = next.UserId, Amount = next.Amount, Status = SecondChanceStatus.Open,
            ExpiresAt = now + OfferLasts, CreatedAt = now,
        };
        db.SecondChanceOffers.Add(offer);
        outbox.Add(SecondChanceTopics.Offered, offer.OfferId.ToString(), Payload(offer));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            var first = await db.SecondChanceOffers.AsNoTracking().FirstAsync(o => o.DefaultedSettlementId == s.SettlementId, ct);
            return await ViewAsync(first, ct);
        }

        PaymentMetrics.SecondChance.Add(1, new KeyValuePair<string, object?>("result", "sent"));
        return await ViewAsync(offer, ct);
    }

    /// <summary>The offer, for its bidder or its seller.</summary>
    public async Task<SecondChanceView> GetAsync(Guid userId, Guid offerId, CancellationToken ct = default)
    {
        var offer = await LoadAsync(offerId, ct);
        return offer.BidderId == userId || offer.SellerId == userId
            ? await ViewAsync(offer, ct)
            : throw new DomainException(ErrorCodes.NotOwner, "This offer is not for you.");
    }

    public async Task<SecondChanceView> AcceptAsync(Guid bidderId, Guid offerId, CancellationToken ct = default)
    {
        var offer = await ForBidderAsync(bidderId, offerId, ct);
        if (offer.Status != SecondChanceStatus.Open)
        {
            return await AnsweredAsync(offer, ct);
        }

        var auction = await db.Auctions.FirstAsync(a => a.AuctionId == offer.AuctionId, ct);   // tracked: saved with the settlement
        var item = await db.Items.AsNoTracking().FirstAsync(i => i.ItemId == auction.ItemId, ct);
        var fees = await db.FeeSchedules.AsNoTracking().FirstAsync(f => f.FeeScheduleId == auction.FeeScheduleId, ct);
        var money = SettlementMath.Compute(offer.Amount, fees.BuyerPremiumPct, fees.SellerFinalValuePct, item.ShippingType, item.ShippingCost);
        var now = clock.GetUtcNow().UtcDateTime;

        var settlement = new Settlement
        {
            SettlementId = Guid.NewGuid(), AuctionId = offer.AuctionId, BuyerId = bidderId, SellerId = offer.SellerId, Currency = auction.Currency,
            HammerPrice = money.HammerPrice, BuyerPremium = money.BuyerPremium, ShippingCost = money.ShippingCost, Tax = money.Tax,
            TotalDue = money.TotalDue, SellerFee = money.SellerFee, InstallmentsAllowed = auction.InstallmentsAllowed,
            Status = SettlementStatus.PendingPayment, PaymentDeadline = now + SettlementMath.PaymentWindow, IsSecondChance = true,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Settlements.Add(settlement);

        var tracked = await db.SecondChanceOffers.FirstAsync(o => o.OfferId == offerId, ct);
        tracked.Status = SecondChanceStatus.Accepted;
        tracked.RespondedAt = now;
        tracked.NewSettlementId = settlement.SettlementId;

        auction.Outcome = AuctionOutcome.Sold;
        auction.UpdatedAt = now;
        outbox.Add(SecondChanceTopics.Accepted, offer.OfferId.ToString(), Payload(tracked));
        try
        {
            await db.SaveChangesAsync(ct);   // UX_Settlement_Active stops a second settlement if two accepts race
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            return await AnsweredAsync(await LoadAsync(offerId, ct), ct);
        }

        PaymentMetrics.SecondChance.Add(1, new KeyValuePair<string, object?>("result", "accepted"));
        return await ViewAsync(tracked, ct);
    }

    public async Task<SecondChanceView> DeclineAsync(Guid bidderId, Guid offerId, CancellationToken ct = default)
    {
        var offer = await ForBidderAsync(bidderId, offerId, ct);
        if (offer.Status != SecondChanceStatus.Open)
        {
            return await AnsweredAsync(offer, ct);
        }

        var tracked = await db.SecondChanceOffers.FirstAsync(o => o.OfferId == offerId, ct);
        tracked.Status = SecondChanceStatus.Declined;
        tracked.RespondedAt = clock.GetUtcNow().UtcDateTime;
        outbox.Add(SecondChanceTopics.Declined, offer.OfferId.ToString(), Payload(tracked));
        await db.SaveChangesAsync(ct);
        PaymentMetrics.SecondChance.Add(1, new KeyValuePair<string, object?>("result", "declined"));
        return await ViewAsync(tracked, ct);
    }

    private async Task<SecondChanceOffer> ForBidderAsync(Guid bidderId, Guid offerId, CancellationToken ct)
    {
        var offer = await LoadAsync(offerId, ct);
        if (offer.BidderId != bidderId)
        {
            throw new DomainException(ErrorCodes.NotOwner, "This offer is not for you.");
        }

        if (offer.Status == SecondChanceStatus.Open && clock.GetUtcNow().UtcDateTime > offer.ExpiresAt)
        {
            throw new DomainException(ErrorCodes.DeadlinePassed, "This offer has expired.");
        }

        return offer;
    }

    /// <summary>Accept or decline again: the first answer stands (R6.6). Expired: too late.</summary>
    private async Task<SecondChanceView> AnsweredAsync(SecondChanceOffer offer, CancellationToken ct) =>
        offer.Status == SecondChanceStatus.Expired
            ? throw new DomainException(ErrorCodes.DeadlinePassed, "This offer has expired.")
            : await ViewAsync(offer, ct);

    private async Task<SecondChanceOffer> LoadAsync(Guid offerId, CancellationToken ct) =>
        await db.SecondChanceOffers.AsNoTracking().FirstOrDefaultAsync(o => o.OfferId == offerId, ct)
        ?? throw new DomainException(ErrorCodes.NotFound, "Offer not found.");

    private async Task<SecondChanceView> ViewAsync(SecondChanceOffer offer, CancellationToken ct) =>
        new(offer, (await settlements.ByIdAsync(offer.DefaultedSettlementId, ct))!);

    private static object Payload(SecondChanceOffer o) => new
    {
        offerId = o.OfferId,
        auctionId = o.AuctionId,
        sellerId = o.SellerId,
        bidderId = o.BidderId,
        amount = o.Amount,
        expiresAt = o.ExpiresAt,
        newSettlementId = o.NewSettlementId,
        status = o.Status.ToString(),
    };
}
