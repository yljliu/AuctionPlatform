using Auction.Domain.Enums;
using Auction.Domain.Identity;
using Auction.Infrastructure.Bidding;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 03 task 10 without SQL or Redis: who sees which name, and Won or Lost.</summary>
public class BidReadUnitTests
{
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Alice = Guid.NewGuid();

    // ---------- names ----------

    [Fact]
    public void Others_see_a_masked_name()
    {
        HandleMask.For("bobsmith42", Bob, viewerId: Alice, viewerIsAdmin: false).Should().Be("b***42");
    }

    [Fact]
    public void Signed_out_viewers_see_a_masked_name()
    {
        HandleMask.For("bobsmith42", Bob, viewerId: null, viewerIsAdmin: false).Should().Be("b***42");
    }

    [Fact]
    public void Bidders_see_their_own_name()
    {
        HandleMask.For("bobsmith42", Bob, viewerId: Bob, viewerIsAdmin: false).Should().Be("bobsmith42");
    }

    [Fact]
    public void Admins_see_every_name()
    {
        HandleMask.For("bobsmith42", Bob, viewerId: Alice, viewerIsAdmin: true).Should().Be("bobsmith42");
    }

    [Fact]
    public void Missing_name_is_safe()
    {
        HandleMask.For(null, Bob, viewerId: null, viewerIsAdmin: false).Should().Be("***");
        HandleMask.For(null, Bob, viewerId: Bob, viewerIsAdmin: false).Should().Be("unknown");
    }

    // ---------- Won or Lost ----------

    [Theory]
    [InlineData(AuctionStatus.Scheduled)]
    [InlineData(AuctionStatus.Live)]
    [InlineData(AuctionStatus.Sealed)]
    [InlineData(AuctionStatus.Settling)]
    [InlineData(AuctionStatus.Frozen)]
    public void Open_auctions_have_no_outcome(AuctionStatus status)
    {
        MyBidRules.OutcomeFor(status, AuctionOutcome.Sold, Bob, Bob).Should().BeEmpty();
    }

    [Fact]
    public void Sold_to_you_is_won()
    {
        MyBidRules.OutcomeFor(AuctionStatus.Settled, AuctionOutcome.Sold, Bob, Bob).Should().Be("Won");
    }

    [Fact]
    public void Sold_to_someone_else_is_lost()
    {
        MyBidRules.OutcomeFor(AuctionStatus.Settled, AuctionOutcome.Sold, Alice, Bob).Should().Be("Lost");
    }

    [Theory]
    [InlineData(AuctionOutcome.Unsold)]
    [InlineData(AuctionOutcome.Defaulted)]
    public void Not_sold_is_lost_even_for_the_leader(AuctionOutcome outcome)
    {
        // For example the reserve was not met.
        MyBidRules.OutcomeFor(AuctionStatus.Settled, outcome, Bob, Bob).Should().Be("Lost");
    }
}
