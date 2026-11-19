using Auction.Web.Services.Live;
using FluentAssertions;
using Xunit;
using static Auction.Web.Tests.LiveTestData;

namespace Auction.Web.Tests;

/// <summary>Spec 04 task 5: applying events to the shared view.</summary>
public class LiveAuctionViewTests
{
    private static LiveAuctionView Start() =>
        LiveAuctionView.Empty(AuctionId).Apply(Snapshot(2, 30m, 2, Row(2, Alice, "a***2x", 30m), Row(1, Bob, "b***42", 10m)));

    [Fact]
    public void Empty_view_has_no_snapshot()
    {
        var view = LiveAuctionView.Empty(AuctionId);

        view.HasSnapshot.Should().BeFalse();
        view.Seq.Should().Be(0);
    }

    [Fact]
    public void Snapshot_fills_the_view()
    {
        var view = Start();

        view.HasSnapshot.Should().BeTrue();
        view.Seq.Should().Be(2);
        view.Status.Should().Be("Live");
        view.Price.Should().Be(30m);
        view.MinNextBid.Should().Be(31m);
        view.LeaderId.Should().Be(Alice);
        view.Rows.Select(r => r.Amount).Should().Equal(30m, 10m);
    }

    [Fact]
    public void Bid_updates_the_price_and_puts_new_rows_first()
    {
        // Event rows come oldest first: Bob's bid, then Alice's proxy answer.
        var view = Start().Apply(Bid(3, 51m, Alice, "a***2x", Row(3, Bob, "b***42", 50m), Row(3, Alice, "a***2x", 51m, auto: true)));

        view.Seq.Should().Be(3);
        view.Price.Should().Be(51m);
        view.MinNextBid.Should().Be(52m);
        view.EndTimeMs.Should().Be(6000);
        view.Rows.Select(r => r.Amount).Should().Equal(51m, 50m, 30m, 10m);
        view.Rows[0].Auto.Should().BeTrue();
    }

    [Fact]
    public void Old_or_repeated_events_change_nothing()
    {
        var view = Start();

        view.Apply(Bid(2, 99m, Bob, "b***42")).Should().BeSameAs(view);
        view.Apply(Bid(1, 99m, Bob, "b***42")).Should().BeSameAs(view);
        view.Apply(State(2, "Sealed")).Should().BeSameAs(view);
    }

    [Fact]
    public void Rows_are_kept_to_the_last_50()
    {
        var view = Start();
        for (var seq = 3; seq < 80; seq++)
        {
            view = view.Apply(Bid(seq, seq, Bob, "b***42", Row(seq, Bob, "b***42", seq)));
        }

        view.Rows.Should().HaveCount(LiveAuctionView.MaxRows);
        view.Rows[0].Seq.Should().Be(79, "newest first");
    }

    [Fact]
    public void State_change_sets_the_status()
    {
        var view = Start().Apply(State(3, "Sealed"));

        view.Status.Should().Be("Sealed");
        view.Seq.Should().Be(3);
        view.Price.Should().Be(30m);
    }

    [Fact]
    public void Heartbeat_only_moves_the_server_time()
    {
        var view = Start();

        var after = view.Apply(Heartbeat(2));

        after.ServerTsMs.Should().Be(900);
        after.Seq.Should().Be(2);
        after.Price.Should().Be(view.Price);
    }

    [Fact]
    public void A_later_snapshot_replaces_everything()
    {
        var view = Start().Apply(Bid(3, 51m, Alice, "a***2x")).Apply(Snapshot(10, 200m, 9));

        view.Seq.Should().Be(10);
        view.Price.Should().Be(200m);
        view.Rows.Should().BeEmpty();
    }
}
