using Auction.Contracts;
using Auction.Domain.Bidding;
using Auction.Web.Services.Live;
using Auction.Web.Services.Ui;

namespace Auction.Web.Services.Bidding;

/// <summary>What the bid panel shows instead of the form.</summary>
public enum BidPanelMode
{
    Form,
    SignIn,
    OwnListing,
    NotStarted,
    Closing,
    Settled,
    Cancelled,
    Paused,
}

/// <summary>Bid panel rules, kept pure so they have unit tests (R3.3 to R3.5, R3.10 to R3.12).</summary>
public static class BidRules
{
    public const decimal ConfirmFactor = 3m;

    public static BidPanelMode Mode(bool signedIn, bool isSeller, string status) => status switch
    {
        "Sealed" or "Settling" => BidPanelMode.Closing,
        "Settled" => BidPanelMode.Settled,
        "Cancelled" => BidPanelMode.Cancelled,
        "Scheduled" => BidPanelMode.NotStarted,
        "Frozen" => BidPanelMode.Paused,
        _ when isSeller => BidPanelMode.OwnListing,
        _ when !signedIn => BidPanelMode.SignIn,
        _ => BidPanelMode.Form,
    };

    /// <summary>Controls are off while sending, while the feed catches up or reconnects, or when not Live (R3.5).</summary>
    public static bool ControlsEnabled(LiveFeedUpdate update, bool sending) => !sending && LiveDisplay.CanBid(update);

    /// <summary>Min next, +1 increment, +3 increments (design 3).</summary>
    public static IReadOnlyList<decimal> QuickBids(decimal minNext, IReadOnlyList<IncrementTier> tiers)
    {
        var table = Table(tiers);
        if (table == null)
        {
            return [minNext];
        }

        var minC = IncrementTable.ToCents(minNext);
        var step = table.IncrementFor(minC);
        return [minNext, (minC + step) / 100m, (minC + (3 * step)) / 100m];
    }

    /// <summary>Ask first when the max is more than 3 times the price (or the start price with no bids) (R3.4).</summary>
    public static bool NeedsConfirm(decimal max, decimal currentPrice, decimal startingPrice, int bidCount)
    {
        var basis = bidCount == 0 ? startingPrice : currentPrice;
        return basis > 0 && max > ConfirmFactor * basis;
    }

    /// <summary>Outbid by someone else: I led before this update and not after it (design 3).</summary>
    public static bool OutbidByOthers(Guid? me, Guid? leaderBefore, Guid? leaderAfter) =>
        me != null && leaderBefore == me && leaderAfter != null && leaderAfter != me;

    private static IncrementTable? Table(IReadOnlyList<IncrementTier> tiers)
    {
        if (tiers.Count == 0)
        {
            return null;
        }

        try
        {
            return IncrementTable.FromMoney(tiers.Select(t => (UiFormat.Amount(t.From), UiFormat.Amount(t.Increment))));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
