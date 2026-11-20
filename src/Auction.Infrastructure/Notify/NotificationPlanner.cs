using System.Globalization;
using System.Text.Json;
using Auction.Domain.Enums;

namespace Auction.Infrastructure.Notify;

/// <summary>One notification to save (and push to the bell).</summary>
public sealed record NotificationDraft(Guid UserId, NotificationType Type, string EventKey, string Title, string? Body, string Link, Guid AuctionId);

/// <summary>What the notifier needs to know about an auction.</summary>
public sealed record AuctionInfo(Guid AuctionId, string Title, Guid SellerId, string Currency);

/// <summary>Who could be told: watchers and everyone who bid.</summary>
public sealed record Recipients(IReadOnlySet<Guid> Watchers, IReadOnlySet<Guid> Bidders);

/// <summary>An event the notifier reacts to, from the partition stream or the outbox.</summary>
public abstract record NotifyEvent(Guid AuctionId);

/// <summary>A bid that took the lead from someone (bid.lua's "outbid" field).</summary>
public sealed record OutbidEvent(Guid AuctionId, long Seq, Guid NewLeader, Guid OutbidUser, long PriceC) : NotifyEvent(AuctionId);

public sealed record ClosingSoonEvent(Guid AuctionId) : NotifyEvent(AuctionId);

/// <summary>Cancelled, from the stream (Redis) or the outbox (SQL); both give the same event key.</summary>
public sealed record CancelledEvent(Guid AuctionId, Guid? ActorId) : NotifyEvent(AuctionId);

/// <summary>AuctionSettled from the outbox (spec 05 Settler).</summary>
public sealed record SettledEvent(Guid AuctionId, string Outcome, decimal Price, Guid? WinnerId, Guid SellerId) : NotifyEvent(AuctionId);

/// <summary>PaymentReceived from the outbox (spec 10 PaymentManager).</summary>
public sealed record PaymentReceivedEvent(Guid AuctionId, Guid SettlementId, Guid BuyerId, Guid SellerId, string BuyerHandle, decimal Amount) : NotifyEvent(AuctionId);

/// <summary>PaymentDefaulted from the outbox (spec 10 DefaultSweeper).</summary>
public sealed record PaymentDefaultedEvent(Guid AuctionId, Guid SettlementId, Guid BuyerId, Guid SellerId, int ActiveStrikes, bool Suspended, bool CanOfferSecondChance) : NotifyEvent(AuctionId);

/// <summary>SecondChanceOffered / Accepted / Declined / Expired from the outbox (spec 10 SecondChanceManager).</summary>
public sealed record SecondChanceEvent(Guid AuctionId, Guid OfferId, Guid SellerId, Guid BidderId, decimal Amount, string Status) : NotifyEvent(AuctionId);

/// <summary>
/// Turns one event into notifications (spec 07 R2). Pure: no SQL, no Redis.
/// Event keys make repeats harmless: the same event always gives the same key per user (UX_Notif_Dedup).
/// Nobody is told about their own action (R2.1).
/// </summary>
public static class NotificationPlanner
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Does this event need watchers and bidders from SQL?</summary>
    public static bool NeedsRecipients(NotifyEvent e) => e is ClosingSoonEvent or CancelledEvent or SettledEvent;

    public static IReadOnlyList<NotificationDraft> Plan(NotifyEvent e, AuctionInfo auction, Recipients who)
    {
        var id = auction.AuctionId;
        var page = $"/a/{id}";
        var title = auction.Title;
        var list = new List<NotificationDraft>();

        switch (e)
        {
            case OutbidEvent o when o.OutbidUser != o.NewLeader:
                list.Add(new(o.OutbidUser, NotificationType.Outbid, $"outbid:{id}:{o.Seq}", $"You've been outbid on {title}",
                    $"The price is now {Money(o.PriceC / 100m, auction.Currency)}.", page, id));
                break;

            case ClosingSoonEvent:
                // One key per auction, so an extension never sends a second one.
                foreach (var user in Everyone(who).Where(u => u != auction.SellerId))
                {
                    list.Add(new(user, NotificationType.EndingSoon, $"soon:{id}", $"{title} ends in 1 minute", null, page, id));
                }

                break;

            case CancelledEvent c:
                foreach (var user in Everyone(who).Where(u => u != auction.SellerId && u != c.ActorId))
                {
                    list.Add(new(user, NotificationType.AuctionCancelled, $"cancelled:{id}", $"{title} was cancelled", null, page, id));
                }

                break;

            case SettledEvent s when s.Outcome == "Sold" && s.WinnerId != null:
                var price = Money(s.Price, auction.Currency);
                list.Add(new(s.WinnerId.Value, NotificationType.Won, $"won:{id}", $"You won {title} for {price}",
                    "Pay within 72 hours.", "/me/purchases", id));
                foreach (var user in who.Bidders.Where(u => u != s.WinnerId && u != s.SellerId))
                {
                    list.Add(new(user, NotificationType.Lost, $"lost:{id}", $"{title} sold for {price}", null, page, id));
                }

                list.Add(new(s.SellerId, NotificationType.AuctionSold, $"sold:{id}", $"Your item sold for {price}", title, "/sell/sales", id));
                break;

            case SettledEvent s:
                foreach (var user in who.Bidders.Where(u => u != s.SellerId))
                {
                    list.Add(new(user, NotificationType.Lost, $"lost:{id}", $"{title} ended without a sale", null, page, id));
                }

                list.Add(new(s.SellerId, NotificationType.AuctionUnsold, $"unsold:{id}", "Your item did not sell", title, "/sell", id));
                break;

            // ---------- spec 10 ----------
            case PaymentReceivedEvent p:
                list.Add(new(p.BuyerId, NotificationType.PaymentReceived, $"paid:{p.SettlementId}", $"Payment received for {title}",
                    $"You paid {Money(p.Amount, auction.Currency)}.", $"/me/purchases/{p.SettlementId}/receipt", id));
                list.Add(new(p.SellerId, NotificationType.BuyerPaid, $"paid-seller:{p.SettlementId}", $"{p.BuyerHandle} paid for {title}",
                    $"{Money(p.Amount, auction.Currency)} received.", "/sell/sales", id));
                break;

            case PaymentDefaultedEvent d:
                list.Add(new(d.SellerId, NotificationType.BuyerDefaulted, $"default-seller:{d.SettlementId}", $"The buyer did not pay for {title}",
                    d.CanOfferSecondChance ? "You can send a second-chance offer to the next bidder." : null, "/sell/sales", id));
                list.Add(d.Suspended
                    ? new(d.BuyerId, NotificationType.AccountSuspended, $"default:{d.SettlementId}", "Your account is suspended: 3 unpaid wins",
                        "You can write an appeal to the admins.", "/account/appeal", id)
                    : new(d.BuyerId, NotificationType.Strike, $"default:{d.SettlementId}",
                        $"You got a strike for not paying for {title} ({d.ActiveStrikes} of 3)", "At 3 strikes the account is suspended.", "/me/purchases", id));
                break;

            case SecondChanceEvent { Status: "Open" } o:
                list.Add(new(o.BidderId, NotificationType.SecondChance, $"sco:{o.OfferId}", $"Second chance: buy {title} for {Money(o.Amount, auction.Currency)}",
                    "The offer lasts 48 hours.", $"/me/offers/{o.OfferId}", id));
                break;

            case SecondChanceEvent o:
                var answer = o.Status switch
                {
                    "Accepted" => $"The bidder accepted your offer for {title}",
                    "Declined" => $"The bidder declined your offer for {title}",
                    _ => $"Your offer for {title} expired",
                };
                list.Add(new(o.SellerId, NotificationType.SecondChanceAnswer, $"sco-{o.Status.ToLowerInvariant()}:{o.OfferId}", answer,
                    o.Status == "Accepted" ? "They have 72 hours to pay." : "You may relist the item.", "/sell/sales", id));
                break;
        }

        return list;
    }

    /// <summary>"$1,250" for whole amounts, "$1,250.50" otherwise; other currencies get their code.</summary>
    public static string Money(decimal amount, string currency)
    {
        var format = amount == decimal.Truncate(amount) ? "N0" : "N2";
        return currency == "USD" ? "$" + amount.ToString(format, Us) : $"{amount.ToString(format, Us)} {currency}";
    }

    /// <summary>Reads an outbox row into an event; null for topics the notifier does not use.</summary>
    public static NotifyEvent? FromOutbox(string topic, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            Guid? GuidOf(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null;

            var auctionId = GuidOf("auctionId");
            if (auctionId == null)
            {
                return null;
            }

            return topic switch
            {
                "AuctionSettled" => new SettledEvent(auctionId.Value,
                    root.TryGetProperty("outcome", out var o) ? o.GetString() ?? "" : "",
                    root.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : 0m,
                    GuidOf("winnerId"),
                    GuidOf("sellerId") ?? Guid.Empty),
                "AuctionCancelled" => new CancelledEvent(auctionId.Value, null),
                "PaymentReceived" when GuidOf("settlementId") is { } sid && GuidOf("buyerId") is { } buyer && GuidOf("sellerId") is { } seller =>
                    new PaymentReceivedEvent(auctionId.Value, sid, buyer, seller,
                        root.TryGetProperty("buyerHandle", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString()! : "The buyer",
                        DecimalOf(root, "amount")),
                "PaymentDefaulted" when GuidOf("settlementId") is { } sid && GuidOf("buyerId") is { } buyer && GuidOf("sellerId") is { } seller =>
                    new PaymentDefaultedEvent(auctionId.Value, sid, buyer, seller,
                        root.TryGetProperty("activeStrikes", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 1,
                        root.TryGetProperty("suspended", out var su) && su.ValueKind == JsonValueKind.True,
                        root.TryGetProperty("canOfferSecondChance", out var co) && co.ValueKind == JsonValueKind.True),
                "SecondChanceOffered" or "SecondChanceAccepted" or "SecondChanceDeclined" or "SecondChanceExpired"
                    when GuidOf("offerId") is { } offer && GuidOf("sellerId") is { } seller && GuidOf("bidderId") is { } bidder =>
                    new SecondChanceEvent(auctionId.Value, offer, seller, bidder, DecimalOf(root, "amount"), topic switch
                    {
                        "SecondChanceOffered" => "Open",
                        "SecondChanceAccepted" => "Accepted",
                        "SecondChanceDeclined" => "Declined",
                        _ => "Expired",
                    }),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a partition stream entry into an event; null for entries the notifier ignores.</summary>
    public static NotifyEvent? FromStream(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("type", out var type) || !fields.TryGetValue("auction", out var a) || !Guid.TryParse(a, out var auctionId))
        {
            return null;
        }

        if (type == "Bid")
        {
            return fields.TryGetValue("outbid", out var outbid) && Guid.TryParse(outbid, out var outbidUser)
                   && fields.TryGetValue("user", out var u) && Guid.TryParse(u, out var bidder)
                   && fields.TryGetValue("seq", out var seqText) && long.TryParse(seqText, out var seq)
                ? new OutbidEvent(auctionId, seq, bidder, outbidUser, fields.TryGetValue("priceC", out var pc) && long.TryParse(pc, out var priceC) ? priceC : 0)
                : null;
        }

        if (type == "State" && fields.TryGetValue("change", out var change))
        {
            return change switch
            {
                "ClosingSoon" => new ClosingSoonEvent(auctionId),
                "Cancelled" => new CancelledEvent(auctionId, null),
                _ => null,
            };
        }

        return null;
    }

    private static IEnumerable<Guid> Everyone(Recipients who) => who.Watchers.Union(who.Bidders);

    private static decimal DecimalOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
}
