using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

public class AuctionEditCancelRulesTests
{
    [Fact]
    public void Scheduled_auction_can_be_edited()
    {
        var act = () => AuctionRules.EnsureEditable(AuctionStatus.Scheduled, 0);
        act.Should().NotThrow();
    }

    [Fact]
    public void Auction_with_bids_is_edit_locked()
    {
        var act = () => AuctionRules.EnsureEditable(AuctionStatus.Live, 3);
        act.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.EditLocked);
    }

    [Theory]
    [InlineData(AuctionStatus.Live)]
    [InlineData(AuctionStatus.Sealed)]
    [InlineData(AuctionStatus.Settled)]
    [InlineData(AuctionStatus.Cancelled)]
    public void Started_or_ended_auction_without_bids_is_invalid_state(AuctionStatus status)
    {
        var act = () => AuctionRules.EnsureEditable(status, 0);
        act.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.InvalidState);
    }

    [Fact]
    public void Scheduled_cancel_is_sql_only()
    {
        AuctionRules.EnsureCancellable(AuctionStatus.Scheduled, 0).Should().Be(CancelPath.Scheduled);
    }

    [Fact]
    public void Live_cancel_without_bids_needs_the_live_copy()
    {
        AuctionRules.EnsureCancellable(AuctionStatus.Live, 0).Should().Be(CancelPath.LiveWithoutBids);
    }

    [Theory]
    [InlineData(AuctionStatus.Live, 1)]
    [InlineData(AuctionStatus.Sealed, 0)]
    [InlineData(AuctionStatus.Settling, 0)]
    [InlineData(AuctionStatus.Settled, 0)]
    [InlineData(AuctionStatus.Cancelled, 0)]
    [InlineData(AuctionStatus.Frozen, 0)]
    public void Other_cancels_are_invalid_state(AuctionStatus status, int bids)
    {
        var act = () => AuctionRules.EnsureCancellable(status, bids);
        act.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.InvalidState);
    }
}
