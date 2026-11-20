using Auction.Domain.Payments;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 10 task 1: the fake payment API's card rules (R2.3) and that only the last 4 are kept (R2.6).</summary>
public class FakeCardCheckTests
{
    private static readonly DateOnly Today = new(2026, 11, 19);

    private static IEnumerable<string> Fields(string? number = "1234567890123456", string? expiry = "12/30", string? cvc = "123", string? name = "Alice Smith") =>
        FakeCardCheck.Check(number, expiry, cvc, name, Today).Select(i => i.Field);

    [Theory]
    [InlineData("1234567890123456")]
    [InlineData("1234 5678 9012 3456")]
    [InlineData("1234-5678-9012-3456")]
    [InlineData("0000000000000000")]
    public void Sixteen_digits_pass(string number) => Fields(number).Should().BeEmpty();

    [Theory]
    [InlineData("123456789012345")]      // 15
    [InlineData("12345678901234567")]    // 17
    [InlineData("1234 5678 9012 345a")]
    [InlineData("1234.5678.9012.3456")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_fails_the_card_number(string? number) =>
        Fields(number).Should().Equal("card_number");

    [Theory]
    [InlineData("11/26", true)]    // this month
    [InlineData("10/26", false)]   // last month
    [InlineData("01/27", true)]
    [InlineData("13/30", false)]
    [InlineData("00/30", false)]
    [InlineData("1/30", false)]
    [InlineData("12-30", false)]
    [InlineData("12/3x", false)]
    [InlineData(null, false)]
    public void Expiry_must_be_a_future_MM_YY(string? expiry, bool ok) =>
        Fields(expiry: expiry).Should().BeEquivalentTo(ok ? [] : new[] { "expiry" });

    [Theory]
    [InlineData("12")]
    [InlineData("1234")]
    [InlineData("12a")]
    [InlineData(null)]
    public void Cvc_must_be_3_digits(string? cvc) => Fields(cvc: cvc).Should().Equal("cvc");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Name_is_required(string? name) => Fields(name: name).Should().Equal("name");

    [Fact]
    public void Name_over_100_fails() => Fields(name: new string('a', 101)).Should().Equal("name");

    [Fact]
    public void Every_bad_field_gets_its_own_message()
    {
        var issues = FakeCardCheck.Check("123", "13/99", "1", "", Today);

        issues.Select(i => i.Field).Should().Equal("card_number", "expiry", "cvc", "name");
        issues[0].Message.Should().Be("Card number must be 16 digits.");
    }

    [Fact]
    public void Last4_ignores_spaces_and_dashes() =>
        FakeCardCheck.Last4("1234 5678-9012 3456").Should().Be("3456");
}
