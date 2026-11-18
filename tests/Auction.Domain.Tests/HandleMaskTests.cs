using Auction.Domain.Identity;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

public class HandleMaskTests
{
    [Theory]
    [InlineData("bobsmith42", "b***42")]
    [InlineData("alice", "a***ce")]
    [InlineData("abcd", "a***cd")]
    [InlineData("abc", "a***")]
    [InlineData("x", "x***")]
    [InlineData("", "***")]
    [InlineData(null, "***")]
    public void Handles_are_masked(string? username, string expected)
    {
        HandleMask.Mask(username).Should().Be(expected);
    }

    // Who sees the real name: see BidReadUnitTests (HandleMask.For).
}
