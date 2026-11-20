using System.Security.Claims;

namespace Auction.Web.Services.Ui;

/// <summary>
/// Bidder or seller mode (design W9 follow-up). A seller picks which side of the site the menu shows; a bidder is
/// always in bidder mode. The mode is a cookie claim, set by POST /account/mode. Pure, so it is unit tested.
/// </summary>
public static class AccountMode
{
    public const string Claim = "mode";
    public const string Bidder = "Bidder";
    public const string Seller = "Seller";

    /// <summary>The mode to use: a seller is in seller mode unless they chose bidder; everyone else is a bidder.</summary>
    public static string Resolve(bool isSeller, string? chosen) =>
        isSeller && !string.Equals(chosen, Bidder, StringComparison.OrdinalIgnoreCase) ? Seller : Bidder;

    public static string Of(ClaimsPrincipal user) => Resolve(user.IsInRole("Seller"), user.FindFirst(Claim)?.Value);

    /// <summary>The Seller hub link shows only in seller mode.</summary>
    public static bool ShowSellerHub(ClaimsPrincipal user) => Of(user) == Seller;

    /// <summary>"Become a seller" shows to everyone who is not a seller yet.</summary>
    public static bool ShowBecomeSeller(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && !user.IsInRole("Seller");

    /// <summary>The other mode, for the settings switch.</summary>
    public static string Other(string mode) => mode == Seller ? Bidder : Seller;
}
