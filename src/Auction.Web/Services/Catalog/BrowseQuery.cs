using System.Globalization;
using Auction.Contracts;

namespace Auction.Web.Services.Catalog;

/// <summary>
/// Browse filters as they appear in the URL (design 5, R2.2), so a link shows the same results.
/// ?q=lamp&amp;status=Live&amp;min=10&amp;max=50&amp;ending=3600&amp;cond=New,Good&amp;sort=price_asc&amp;page=2
/// </summary>
public sealed record BrowseQuery(
    string? Q = null,
    string? Status = null,
    decimal? Min = null,
    decimal? Max = null,
    int? Ending = null,
    IReadOnlyList<string>? Conditions = null,
    string? Sort = null,
    int Page = 1)
{
    public const int PageSize = 12;

    public static readonly IReadOnlyList<(string Value, string Label)> Sorts =
    [
        ("ending", "Ending soon"), ("newest", "Newest"), ("price_asc", "Price: low to high"),
        ("price_desc", "Price: high to low"), ("bids", "Most bids"),
    ];

    public static readonly IReadOnlyList<string> Statuses = ["Live", "Scheduled", "Settled"];

    public static readonly IReadOnlyList<string> ConditionNames = ["New", "LikeNew", "Good", "Fair", "ForParts"];

    public static readonly IReadOnlyList<(int Seconds, string Label)> EndingOptions =
    [
        (3600, "1 hour"), (6 * 3600, "6 hours"), (86400, "1 day"), (7 * 86400, "7 days"),
    ];

    public bool IsSearch => (Q?.Trim().Length ?? 0) >= 2;

    public bool HasFilters => Q != null || Status != null || Min != null || Max != null || Ending != null || Conditions is { Count: > 0 } || Sort != null;

    /// <summary>
    /// True when the two differ only by page. A filter control can report its value again on a re-render (for example the
    /// Status select on page 2); that must not count as a change, or it sends the user back to page 1.
    /// </summary>
    public bool SameFiltersAs(BrowseQuery other)
    {
        var a = ToQuery();
        var b = other.ToQuery();
        return a.Keys.Where(k => k != "page").All(k => Equals(a[k], b[k]));
    }

    public static BrowseQuery Parse(string? q, string? status, string? min, string? max, string? ending, string? cond, string? sort, string? page) => new(
        Q: string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
        Status: Statuses.Contains(status) ? status : null,
        Min: decimal.TryParse(min, NumberStyles.Number, CultureInfo.InvariantCulture, out var mn) && mn >= 0 ? mn : null,
        Max: decimal.TryParse(max, NumberStyles.Number, CultureInfo.InvariantCulture, out var mx) && mx >= 0 ? mx : null,
        Ending: int.TryParse(ending, out var e) && e > 0 ? e : null,
        Conditions: string.IsNullOrWhiteSpace(cond) ? null : cond.Split(',').Where(c => ConditionNames.Contains(c)).Distinct().ToList(),
        Sort: Sorts.Any(s => s.Value == sort) ? sort : null,
        Page: int.TryParse(page, out var p) && p > 0 ? p : 1);

    /// <summary>The query string values; null removes a key from the URL.</summary>
    public IReadOnlyDictionary<string, object?> ToQuery() => new Dictionary<string, object?>
    {
        ["q"] = Q,
        ["status"] = Status,
        ["min"] = Min?.ToString(CultureInfo.InvariantCulture),
        ["max"] = Max?.ToString(CultureInfo.InvariantCulture),
        ["ending"] = Ending?.ToString(CultureInfo.InvariantCulture),
        ["cond"] = Conditions is { Count: > 0 } ? string.Join(',', Conditions) : null,
        ["sort"] = Sort,
        ["page"] = Page > 1 ? Page.ToString(CultureInfo.InvariantCulture) : null,
    };

    public AuctionFilter ToFilter(int? categoryId)
    {
        var filter = new AuctionFilter { Status = Status ?? "" };
        if (categoryId != null)
        {
            filter.CategoryId = categoryId;
        }

        if (Min != null)
        {
            filter.MinPrice = new Domain.Money(Min.Value, "USD").ToProto();
        }

        if (Max != null)
        {
            filter.MaxPrice = new Domain.Money(Max.Value, "USD").ToProto();
        }

        if (Ending != null)
        {
            filter.EndingWithinSec = Ending;
        }

        filter.Conditions.AddRange(Conditions ?? []);
        return filter;
    }

    public AuctionSort ToSort() => Sort switch
    {
        "newest" => AuctionSort.Newest,
        "price_asc" => AuctionSort.PriceAsc,
        "price_desc" => AuctionSort.PriceDesc,
        "bids" => AuctionSort.MostBids,
        _ => AuctionSort.EndingSoon,
    };
}
