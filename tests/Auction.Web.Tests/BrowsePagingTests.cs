using Auction.Web.Services.Catalog;
using FluentAssertions;
using Xunit;

namespace Auction.Web.Tests;

/// <summary>Page 2 bug: a filter control reporting the same value again must not send you back to page 1.</summary>
public class BrowsePagingTests
{
    [Fact]
    public void Only_the_page_differs_so_the_filters_are_the_same()
    {
        var page2 = new BrowseQuery(Status: "Scheduled", Page: 2);

        page2.SameFiltersAs(page2 with { Page = 1 }).Should().BeTrue();
        page2.SameFiltersAs(new BrowseQuery(Status: "Scheduled", Page: 2)).Should().BeTrue();
    }

    [Fact]
    public void Conditions_in_new_lists_with_the_same_values_are_the_same()
    {
        var a = new BrowseQuery(Conditions: ["New", "Good"], Page: 3);
        var b = new BrowseQuery(Conditions: new List<string> { "New", "Good" });

        a.SameFiltersAs(b).Should().BeTrue();
    }

    [Theory]
    [InlineData("Live")]
    [InlineData("Settled")]
    public void A_real_status_change_is_a_change(string status) =>
        new BrowseQuery(Status: "Scheduled", Page: 2).SameFiltersAs(new BrowseQuery(Status: status)).Should().BeFalse();

    [Fact]
    public void Price_and_sort_changes_are_changes()
    {
        var q = new BrowseQuery(Status: "Scheduled", Page: 2);

        q.SameFiltersAs(q with { Min = 10m }).Should().BeFalse();
        q.SameFiltersAs(q with { Sort = "price_asc" }).Should().BeFalse();
        q.SameFiltersAs(q with { Q = "lamp" }).Should().BeFalse();
    }
}
