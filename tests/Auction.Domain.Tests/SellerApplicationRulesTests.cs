using Auction.Domain.Identity;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Become a seller (design W9): the shop name rules.</summary>
public class SellerApplicationRulesTests
{
    [Theory]
    [InlineData("Joe's Watches")]
    [InlineData("A&B Coins")]
    [InlineData("Vintage-2026.co")]
    [InlineData("abc")]
    public void Good_names_pass(string name) => SellerApplicationRules.CheckDisplayName(name).Should().BeNull();

    [Theory]
    [InlineData(null, "length")]
    [InlineData("ab", "length")]
    [InlineData("   ab   ", "length")]
    [InlineData("12345", "letter")]
    [InlineData("Shop <script>", "chars")]
    [InlineData("Best_Shop", "chars")]
    public void Bad_names_say_why(string? name, string rule) =>
        SellerApplicationRules.CheckDisplayName(name)!.Rule.Should().Be(rule);

    [Fact]
    public void Forty_is_the_limit()
    {
        SellerApplicationRules.CheckDisplayName(new string('a', 40)).Should().BeNull();
        SellerApplicationRules.CheckDisplayName(new string('a', 41))!.Field.Should().Be("display_name");
    }

    [Fact]
    public void Clean_trims_and_joins_spaces() =>
        SellerApplicationRules.Clean("  Joe's    Watches ").Should().Be("Joe's Watches");
}
