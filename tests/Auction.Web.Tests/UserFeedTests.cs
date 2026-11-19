using System.Threading.Channels;
using Auction.Contracts;
using Auction.Web.Services.Notify;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Auction.Web.Tests.LiveTestData;

namespace Auction.Web.Tests;

/// <summary>Spec 07 task 5: the bell feed's count, latest list, popups and suppression (R3.1 to R3.3).</summary>
public class UserFeedTests
{
    private sealed class FakeSource : IUserFeedSource
    {
        public Channel<UserEvent> Live { get; } = Channel.CreateUnbounded<UserEvent>();

        public int Unread { get; set; }

        public List<NotificationItem> Saved { get; } = [];

        public int Reloads { get; private set; }

        public Task<ListNotificationsResponse> LatestAsync(int count, CancellationToken ct)
        {
            Reloads++;
            var response = new ListNotificationsResponse { Unread = Unread, Total = Saved.Count };
            response.Items.AddRange(Saved.Take(count));
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<UserEvent> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var e in Live.Reader.ReadAllAsync(ct))
            {
                yield return e;
            }
        }

        public Task<int> MarkReadAsync(long id, CancellationToken ct) => Task.FromResult(Math.Max(0, --Unread));

        public Task<int> MarkAllReadAsync(CancellationToken ct) => Task.FromResult(Unread = 0);
    }

    private static NotificationItem Item(long id, string type = "Outbid", Guid? auction = null) => new()
    {
        NotificationId = id, Type = type, Title = $"n{id}", AuctionId = (auction ?? AuctionId).ToString(), Link = $"/a/{auction ?? AuctionId}",
    };

    private static UserFeed Feed(FakeSource source) => new(source, NullLogger<UserFeed>.Instance);

    [Fact]
    public void A_new_notification_counts_goes_on_top_and_pops_up()
    {
        var feed = Feed(new FakeSource());
        var popups = new List<bool>();
        feed.Changed += (item, popup) => { if (item != null) popups.Add(popup); };

        feed.Receive(Item(1));
        feed.Receive(Item(2, "Won"));

        feed.Unread.Should().Be(2);
        feed.Latest.Select(n => n.NotificationId).Should().Equal(2L, 1L);
        popups.Should().Equal(true, true);
    }

    [Fact]
    public void While_the_auction_page_is_open_its_outbid_counts_but_does_not_pop_up()
    {
        var feed = Feed(new FakeSource());
        var popups = new List<bool>();
        feed.Changed += (item, popup) => { if (item != null) popups.Add(popup); };
        var other = Guid.NewGuid();

        using (feed.SuppressFor(AuctionId))
        {
            feed.Receive(Item(1));                       // outbid on the open auction: quiet
            feed.Receive(Item(2, "EndingSoon"));         // not an outbid: pops up
            feed.Receive(Item(3, "Outbid", other));      // another auction: pops up
        }

        feed.Receive(Item(4));                           // page closed: pops up again

        popups.Should().Equal(false, true, true, true);
        feed.Unread.Should().Be(4, "every one still counts");
    }

    [Fact]
    public void Two_pages_on_the_same_auction_both_have_to_close()
    {
        var feed = Feed(new FakeSource());
        var first = feed.SuppressFor(AuctionId);
        var second = feed.SuppressFor(AuctionId);

        first.Dispose();
        first.Dispose();   // twice is harmless
        feed.IsSuppressed(AuctionId).Should().BeTrue();

        second.Dispose();
        feed.IsSuppressed(AuctionId).Should().BeFalse();
    }

    [Fact]
    public void The_latest_list_keeps_10_and_skips_repeats()
    {
        var feed = Feed(new FakeSource());

        for (var i = 1; i <= 12; i++)
        {
            feed.Receive(Item(i, "Won"));
        }

        feed.Receive(Item(12, "Won"));

        feed.Latest.Should().HaveCount(UserFeed.LatestCount);
        feed.Latest[0].NotificationId.Should().Be(12);
        feed.Unread.Should().Be(12, "the repeat is not counted");
    }

    [Fact]
    public async Task Start_loads_missed_ones_then_streams_new_ones()
    {
        var source = new FakeSource { Unread = 3 };
        source.Saved.Add(Item(9, "Won"));
        await using var feed = Feed(source);

        feed.Start();
        await Wait.UntilAsync(() => source.Reloads == 1, "the first load");
        feed.Unread.Should().Be(3, "missed ones show when the site opens (R3.2)");
        feed.Latest.Should().ContainSingle();

        source.Live.Writer.TryWrite(new UserEvent { Notification = Item(10, "Lost") });
        await Wait.UntilAsync(() => feed.Unread == 4, "the live one");
        feed.Latest[0].NotificationId.Should().Be(10);
    }

    [Fact]
    public async Task A_broken_stream_reconnects_and_reloads()
    {
        var source = new FakeSource();
        await using var feed = Feed(source);
        feed.Backoff = _ => TimeSpan.FromMilliseconds(10);

        feed.Start();
        await Wait.UntilAsync(() => source.Reloads == 1, "the first load");
        source.Live.Writer.TryComplete(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "api down")));

        await Wait.UntilAsync(() => source.Reloads >= 2, "a reload after reconnecting");
    }

    [Fact]
    public async Task Mark_all_read_clears_the_count_and_the_bold()
    {
        var source = new FakeSource { Unread = 2 };
        var feed = Feed(source);
        feed.Receive(Item(1, "Won"));
        feed.Receive(Item(2, "Won"));

        await feed.MarkAllReadAsync();

        feed.Unread.Should().Be(0);
        feed.Latest.Should().OnlyContain(n => n.Read);
    }
}
