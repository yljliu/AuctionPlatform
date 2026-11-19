using Auction.Contracts;
using Auction.Web.Services.Ui;
using MudBlazor;

namespace Auction.Web.Services.Bidding;

/// <summary>Every bid snackbar text in one place (design 3, R3.7).</summary>
public static class BidMessages
{
    public sealed record Message(string Text, Severity Severity);

    public static Message ForAccepted(PlaceBidResponse reply, string currency)
    {
        var price = UiFormat.Money(UiFormat.Amount(reply.CurrentPrice), currency);
        var text = reply.Result switch
        {
            PlaceBidResponse.Types.Result.Leading => $"You're leading at {price}.",
            PlaceBidResponse.Types.Result.OutbidByProxy => $"Someone's max is higher. Price now {price}.",
            PlaceBidResponse.Types.Result.MaxRaised => "Your max is raised. You're still leading.",
            _ => $"Bid placed. Price now {price}.",
        };

        if (reply.Extended)
        {
            text += " The auction was extended.";
        }

        var severity = reply.Result == PlaceBidResponse.Types.Result.OutbidByProxy ? Severity.Warning : Severity.Success;
        return new Message(text, severity);
    }

    /// <summary>The error text for a code, with "Ref: ..." when the Api sent a correlation id (R8.1).</summary>
    public static Message ForError(UiError error)
    {
        var text = error.Code switch
        {
            "BELOW_MINIMUM" => "Your bid is below the minimum. Try a higher amount.",
            "NOT_LIVE" => "This auction is not open for bids.",
            "ENDED" => "This auction has ended.",
            "AUCTION_FROZEN" => "Bidding is paused on this auction.",
            "OWN_LOT" => "You can't bid on your own listing.",
            "IDEMPOTENCY_MISMATCH" => "That bid was already sent with a different amount. Please try again.",
            "RATE_LIMITED" => "Too many bids at once. Wait a moment and try again.",
            "UNAUTHENTICATED" => "Please sign in again to bid.",
            "ACCOUNT_SUSPENDED" => "Your account is suspended, so you can't bid.",
            "ACCOUNT_BANNED" => "Your account is banned.",
            "EMAIL_NOT_VERIFIED" => "Verify your email before you bid.",
            "VALIDATION_FAILED" => error.Message,
            "SERVICE_UNAVAILABLE" or "TIMEOUT" => "The bid service is busy. Please try again.",
            _ => error.Message,
        };

        var withRef = string.IsNullOrEmpty(error.CorrelationId) ? text : $"{text} (Ref: {error.CorrelationId})";
        return new Message(withRef, Severity.Error);
    }

    public const string Unknown = "Couldn't confirm your bid. Retry? (It will not be counted twice.)";

    public static string OutbidByOthers(decimal newMin, string currency) =>
        $"You've been outbid. Bid {UiFormat.Money(newMin, currency)} to lead again?";
}
