using Auction.Domain.Errors;

namespace Auction.Domain.Payments;

/// <summary>
/// The fake payment API's card rules (spec 10 R2.3). Pure, so the Api decides and the Web page can show the same hints.
/// Any 16-digit number is a "good" card; nothing is charged anywhere.
/// </summary>
public static class FakeCardCheck
{
    public const int NameMax = 100;

    /// <summary>Every broken rule, one issue per field. Empty means the card is accepted.</summary>
    public static IReadOnlyList<FieldIssue> Check(string? number, string? expiry, string? cvc, string? name, DateOnly today)
    {
        var issues = new List<FieldIssue>();

        if (Digits(number) is not { Length: 16 })
        {
            issues.Add(new FieldIssue("card_number", "length", "Card number must be 16 digits."));
        }

        if (!ExpiryOk(expiry, today))
        {
            issues.Add(new FieldIssue("expiry", "future", "Expiry must be a future MM/YY."));
        }

        if (cvc is not { Length: 3 } || !cvc.All(char.IsAsciiDigit))
        {
            issues.Add(new FieldIssue("cvc", "length", "CVC must be 3 digits."));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMax)
        {
            issues.Add(new FieldIssue("name", "required", "Enter the name on the card."));
        }

        return issues;
    }

    /// <summary>The last 4 digits, the only part of the card that is ever kept (R2.6).</summary>
    public static string Last4(string number)
    {
        var digits = Digits(number) ?? throw new ArgumentException("Not a card number.", nameof(number));
        return digits[^4..];
    }

    /// <summary>The number with spaces and dashes taken out, or null if anything else is in it.</summary>
    private static string? Digits(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        var kept = new char[number.Length];
        var n = 0;
        foreach (var c in number)
        {
            if (char.IsAsciiDigit(c))
            {
                kept[n++] = c;
            }
            else if (c is not (' ' or '-'))
            {
                return null;
            }
        }

        return new string(kept, 0, n);
    }

    /// <summary>MM/YY, month 01 to 12, this month or later.</summary>
    private static bool ExpiryOk(string? expiry, DateOnly today)
    {
        if (expiry is not { Length: 5 } || expiry[2] != '/'
            || !int.TryParse(expiry.AsSpan(0, 2), System.Globalization.NumberStyles.None, null, out var month)
            || !int.TryParse(expiry.AsSpan(3, 2), System.Globalization.NumberStyles.None, null, out var yy)
            || month is < 1 or > 12)
        {
            return false;
        }

        var year = 2000 + yy;
        return year > today.Year || (year == today.Year && month >= today.Month);
    }
}
