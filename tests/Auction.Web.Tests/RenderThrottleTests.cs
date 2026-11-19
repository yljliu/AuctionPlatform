using Auction.Web.Services.Live;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Auction.Web.Tests.LiveTestData;

namespace Auction.Web.Tests;

/// <summary>Spec 04 task 6: at most 10 redraws a second, "you" marking, bid button rule.</summary>
public class RenderThrottleTests
{
    [Fact]
    public void Many_changes_in_100_ms_give_one_redraw()
    {
        var clock = new FakeTimeProvider();
        var renders = 0;
        using var throttle = new RenderThrottle(RenderThrottle.Default, clock, () => { renders++; return Task.CompletedTask; });

        for (var i = 0; i < 50; i++)
        {
            throttle.MarkDirty();
        }

        renders.Should().Be(0, "nothing is drawn before the tick");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        renders.Should().Be(1);
    }

    [Fact]
    public void No_changes_means_no_redraw()
    {
        var clock = new FakeTimeProvider();
        var renders = 0;
        using var throttle = new RenderThrottle(RenderThrottle.Default, clock, () => { renders++; return Task.CompletedTask; });

        clock.Advance(TimeSpan.FromSeconds(1));

        renders.Should().Be(0);
    }

    [Fact]
    public void Changes_all_second_long_give_at_most_10_redraws()
    {
        var clock = new FakeTimeProvider();
        var renders = 0;
        using var throttle = new RenderThrottle(RenderThrottle.Default, clock, () => { renders++; return Task.CompletedTask; });

        for (var ms = 0; ms < 1000; ms += 5)
        {
            throttle.MarkDirty();
            clock.Advance(TimeSpan.FromMilliseconds(5));
        }

        renders.Should().Be(10);
    }

    [Fact]
    public void Disposed_throttle_stops_drawing()
    {
        var clock = new FakeTimeProvider();
        var renders = 0;
        var throttle = new RenderThrottle(RenderThrottle.Default, clock, () => { renders++; return Task.CompletedTask; });

        throttle.MarkDirty();
        throttle.Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));

        renders.Should().Be(0);
    }

    // ---------- "you" and the bid button ----------

    [Fact]
    public void Your_own_rows_show_you()
    {
        LiveDisplay.NameFor(Alice, "a***2x", me: Alice).Should().Be("you");
        LiveDisplay.NameFor(Bob, "b***42", me: Alice).Should().Be("b***42");
        LiveDisplay.NameFor(Bob, "b***42", me: null).Should().Be("b***42");
    }

    private static LiveFeedUpdate Update(LiveFeedStatus status, string auctionStatus = "Live", bool snapshot = true)
    {
        var view = LiveAuctionView.Empty(AuctionId);
        if (snapshot)
        {
            view = view.Apply(Snapshot(1));
        }

        return new LiveFeedUpdate(view with { Status = auctionStatus }, status);
    }

    [Fact]
    public void Bid_button_works_only_when_live_and_caught_up()
    {
        LiveDisplay.CanBid(Update(LiveFeedStatus.Live)).Should().BeTrue();
        LiveDisplay.CanBid(Update(LiveFeedStatus.Reconnecting)).Should().BeFalse("bidding is paused while reconnecting");
        LiveDisplay.CanBid(Update(LiveFeedStatus.Connecting)).Should().BeFalse();
        LiveDisplay.CanBid(Update(LiveFeedStatus.Live, "Sealed")).Should().BeFalse("the auction has closed");
        LiveDisplay.CanBid(Update(LiveFeedStatus.Live, snapshot: false)).Should().BeFalse("nothing loaded yet");
    }
}
