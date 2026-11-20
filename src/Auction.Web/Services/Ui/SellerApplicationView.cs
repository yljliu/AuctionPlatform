namespace Auction.Web.Services.Ui;

/// <summary>Words and rules for the seller application pages (design W9). Pure, so it is unit tested.</summary>
public static class SellerApplicationView
{
    /// <summary>Apply is allowed when there is no application yet, or the last one was rejected.</summary>
    public static bool CanApply(string? status) => status is null or "" or "None" or "Rejected";

    public static string Words(string? status) => status switch
    {
        null or "" or "None" => "not sent",
        "Applied" => "waiting for an admin",
        "Verified" => "approved",
        "Rejected" => "not approved",
        "Revoked" => "turned off",
        var s => s,
    };
}
