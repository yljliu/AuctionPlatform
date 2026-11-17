using Auction.Domain.Catalog;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

public class CategoryTreeTests
{
    private static CategoryRow Row(int id, int? parent, string name, int sort = 0, bool active = true) =>
        new(id, parent, name, name.ToLowerInvariant(), null, sort, active);

    [Fact]
    public void Children_nest_under_their_parent()
    {
        var tree = CategoryTree.Build([Row(1, null, "Watches"), Row(2, 1, "Vintage"), Row(3, 1, "Modern"), Row(4, 2, "Pocket")]);

        tree.Should().ContainSingle().Which.Name.Should().Be("Watches");
        tree[0].Children.Select(c => c.Name).Should().BeEquivalentTo("Modern", "Vintage");
        tree[0].Children.Single(c => c.Name == "Vintage").Children.Should().ContainSingle().Which.Name.Should().Be("Pocket");
    }

    [Fact]
    public void Each_level_sorts_by_sort_order_then_name()
    {
        var tree = CategoryTree.Build(
        [
            Row(1, null, "Coins", sort: 2),
            Row(2, null, "Art", sort: 1),
            Row(3, null, "Watches", sort: 1),
            Row(4, null, "antiques", sort: 1),
        ]);

        tree.Select(c => c.Name).Should().Equal("antiques", "Art", "Watches", "Coins");
    }

    [Fact]
    public void Inactive_category_is_hidden_with_its_children()
    {
        var tree = CategoryTree.Build([Row(1, null, "Art"), Row(2, 1, "Prints", active: false), Row(3, 2, "Posters"), Row(4, 1, "Paintings")]);

        tree[0].Children.Select(c => c.Name).Should().Equal("Paintings");
    }

    [Fact]
    public void Inactive_rows_stay_when_asked()
    {
        var tree = CategoryTree.Build([Row(1, null, "Art"), Row(2, 1, "Prints", active: false)], activeOnly: false);

        tree[0].Children.Should().ContainSingle().Which.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Row_with_a_missing_parent_is_left_out()
    {
        var tree = CategoryTree.Build([Row(1, null, "Art"), Row(2, 99, "Orphan")]);

        tree.Should().ContainSingle().Which.Children.Should().BeEmpty();
    }

    [Fact]
    public void Self_and_descendants_lists_the_whole_branch()
    {
        var tree = CategoryTree.Build([Row(1, null, "Watches"), Row(2, 1, "Vintage"), Row(3, 2, "Pocket"), Row(4, null, "Art")]);

        CategoryTree.SelfAndDescendants(tree, 1).Should().BeEquivalentTo([1, 2, 3]);
        CategoryTree.SelfAndDescendants(tree, 2).Should().BeEquivalentTo([2, 3]);
        CategoryTree.SelfAndDescendants(tree, 42).Should().BeEmpty();
    }
}
