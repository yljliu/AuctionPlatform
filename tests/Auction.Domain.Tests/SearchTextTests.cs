using Auction.Domain.Catalog;
using Auction.Domain.Errors;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

public class SearchTextTests
{
    [Theory]
    [InlineData("a")]
    [InlineData(" a ")]
    [InlineData("")]
    [InlineData(null)]
    public void Shorter_than_2_characters_is_refused(string? query)
    {
        var act = () => SearchText.Words(query);
        act.Should().Throw<DomainException>().Which.Fields.Single().Field.Should().Be("query");
    }

    [Fact]
    public void Longer_than_100_characters_is_refused()
    {
        var act = () => SearchText.Words(new string('x', 101));
        act.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Splits_into_words_without_repeats()
    {
        SearchText.Words("  omega   Seamaster omega ").Should().Equal("omega", "Seamaster");
    }

    [Fact]
    public void Keeps_at_most_5_words()
    {
        SearchText.Words("a1 b2 c3 d4 e5 f6 g7").Should().HaveCount(5);
    }

    [Theory]
    [InlineData("100%", "%100[%]%")]
    [InlineData("a_b", "%a[_]b%")]
    [InlineData("[x]", "%[[]x]%")]
    [InlineData("plain", "%plain%")]
    public void Like_wildcards_become_literal(string word, string pattern)
    {
        SearchText.ContainsPattern(word).Should().Be(pattern);
    }
}
