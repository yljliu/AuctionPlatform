using Auction.Domain.Errors;

namespace Auction.Domain.Catalog;

/// <summary>Search box rules (R6.2) and safe LIKE patterns.</summary>
public static class SearchText
{
    public const int MinLength = 2;
    public const int MaxLength = 100;
    public const int MaxWords = 5;

    /// <summary>
    /// Splits the search into words (at most 5). Throws VALIDATION_FAILED when the text is not 2 to 100 characters.
    /// </summary>
    public static IReadOnlyList<string> Words(string? query)
    {
        var text = (query ?? "").Trim();
        if (text.Length < MinLength || text.Length > MaxLength)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Search must be 2 to 100 characters.",
                [new FieldIssue("query", "length", $"Type {MinLength} to {MaxLength} characters.")]);
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxWords).ToList();
    }

    /// <summary>
    /// "%word%" with the SQL Server LIKE wildcards in the word made literal,
    /// so searching "100%" finds "100%" and not "1000".
    /// </summary>
    public static string ContainsPattern(string word) => "%" + EscapeLike(word) + "%";

    public static string EscapeLike(string text) =>
        text.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
}
