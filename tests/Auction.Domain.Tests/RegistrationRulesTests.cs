using Auction.Domain.Identity;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

public class RegistrationRulesTests
{
    private const string GoodPassword = "correct-horse-battery";

    [Fact]
    public void Good_input_has_no_issues()
    {
        RegistrationRules.Validate("alice_01", "alice@example.com", GoodPassword).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ab", "length")]
    [InlineData("this_username_is_way_too_long_123", "length")]
    [InlineData("bad name", "pattern")]
    [InlineData("bad-name", "pattern")]
    [InlineData("", "length")]
    public void Bad_username(string username, string rule)
    {
        var issues = RegistrationRules.Validate(username, "a@example.com", GoodPassword);

        issues.Should().ContainSingle().Which.Should().Match<Errors.FieldIssue>(i => i.Field == "username" && i.Rule == rule);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("Alice <a@example.com>")]
    public void Bad_email(string email)
    {
        var issues = RegistrationRules.Validate("alice", email, GoodPassword);

        issues.Should().ContainSingle().Which.Field.Should().Be("email");
    }

    [Fact]
    public void Short_password()
    {
        var issues = RegistrationRules.Validate("alice", "a@example.com", "short");

        issues.Should().ContainSingle().Which.Rule.Should().Be("min_length");
    }

    [Fact]
    public void Exactly_12_characters_is_ok()
    {
        RegistrationRules.Validate("alice", "a@example.com", "123456789012").Should().BeEmpty();
    }

    [Fact]
    public void One_issue_per_bad_field()
    {
        var issues = RegistrationRules.Validate("x", "nope", "short");

        issues.Select(i => i.Field).Should().Equal("username", "email", "password");
    }
}
