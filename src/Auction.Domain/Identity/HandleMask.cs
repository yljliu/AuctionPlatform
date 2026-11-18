namespace Auction.Domain.Identity;

/// <summary>
/// Hides bidder names on public pages (design 6.4): first letter + "***" + last 2 letters.
/// "bobsmith42" becomes "b***42". Short names keep only the first letter.
/// </summary>
public static class HandleMask
{
    public static string Mask(string? username)
    {
        if (string.IsNullOrEmpty(username))
        {
            return "***";
        }

        if (username.Length <= 3)
        {
            return username[0] + "***";
        }

        return username[0] + "***" + username[^2..];
    }

    /// <summary>The name a viewer may see: the real one for themselves and for admins, masked for everyone else.</summary>
    public static string For(string? username, Guid bidderId, Guid? viewerId, bool viewerIsAdmin) =>
        viewerIsAdmin || bidderId == viewerId ? username ?? "unknown" : Mask(username);
}
