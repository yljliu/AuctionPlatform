using Auction.Domain.Errors;

namespace Auction.Domain.Identity;

/// <summary>Rules for a bidder's request to become a seller (design W9). Pure, so they are unit tested.</summary>
public static class SellerApplicationRules
{
    public const int NameMin = 3;
    public const int NameMax = 40;
    public const int ReasonMax = 400;

    /// <summary>The shop name as it will be saved: trimmed, inner runs of spaces made single.</summary>
    public static string Clean(string? displayName) =>
        string.Join(' ', (displayName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Null when the shop name is fine, otherwise the problem: 3 to 40 letters, digits, spaces and . - ' &amp;.</summary>
    public static FieldIssue? CheckDisplayName(string? displayName)
    {
        var name = Clean(displayName);
        if (name.Length is < NameMin or > NameMax)
        {
            return new FieldIssue("display_name", "length", $"Use {NameMin} to {NameMax} characters.");
        }

        if (!name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '\'' or '&'))
        {
            return new FieldIssue("display_name", "chars", "Use letters, digits, spaces and . - ' & only.");
        }

        return !name.Any(char.IsLetter)
            ? new FieldIssue("display_name", "letter", "Use at least one letter.")
            : null;
    }
}
