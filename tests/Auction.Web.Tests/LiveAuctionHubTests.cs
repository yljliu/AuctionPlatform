using Auction.Web.Services.Live;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Auction.Web.Tests.LiveTestData;

namespace Auction.Web.Tests;

/// <summary>Spec 04 task 5: one shared stream per auction, reconnect from the last seq, idle close.</summary>
public class LiveAuctionHubTests : IDisposable
{
    private readonly FakeLiveSource _source = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly LiveAuctionHub _hub;

    public LiveAuctionHubTests()
    {
        _hub = new LiveAuctionHub(_source, _clock, new LiveHubOptions(), NullLogger<LiveAuctionHub>.Instance);
    }

    public void Dispose() => _hub.Dispose();

    private sealed class Recorder
    {
        private readonly List<LiveFeedUpdate> _seen = [];

        public void On(LiveFeedUpdate update)
        {
            lock (_seen)
            {
                _seen.Add(update);
            }
        }

        public LiveFeedUpdate? Last
        {
            get
            {
                lock (_seen)
                {
                    return _seen.Count == 0 ? null : _seen[^1];
                }
            }
        }

        public bool Saw(Func<LiveFeedUpdate, bool> check)
        {
            lock (_seen)
            {
                return _seen.Any(check);
            }
        }
    }

    [Fact]
    public async Task Two_watchers_share_one_stream()
    {
        var a = new Recorder();
        var b = new Recorder();

        using var first = _hub.Watch(AuctionId, a.On);
        using var second = _hub.Watch(AuctionId, b.On);
        await Wait.UntilAsync(() => _source.Calls.Count == 1, "the stream to open");

        _source.Push(Snapshot(4));
        await Wait.UntilAsync(() => a.Last?.View.Seq == 4 && b.Last?.View.Seq == 4, "both watchers to get the snapshot");

        _source.Calls.Should().HaveCount(1);
        _hub.OpenStreams.Should().Be(1);
        a.Last!.Status.Should().Be(LiveFeedStatus.Live);
    }

    [Fact]
    public async Task First_connect_asks_from_seq_0()
    {
        using var watch = _hub.Watch(AuctionId, _ => { });
        await Wait.UntilAsync(() => _source.Calls.Count == 1, "the stream to open");

        _source.Latest.LastSeenSeq.Should().Be(0);
        watch.Current.Status.Should().Be(LiveFeedStatus.Connecting);
    }

    [Fact]
    public async Task Late_watcher_gets_the_current_view_at_once()
    {
        using var first = _hub.Watch(AuctionId, _ => { });
        await Wait.UntilAsync(() => _source.Calls.Count == 1, "the stream to open");
        _source.Push(Snapshot(4, 30m));
        await Wait.UntilAsync(() => first.Current.View.Seq == 4, "the snapshot");

        using var late = _hub.Watch(AuctionId, _ => { });

        late.Current.View.Price.Should().Be(30m);
        late.Current.Status.Should().Be(LiveFeedStatus.Live);
        _source.Calls.Should().HaveCount(1, "no second stream for a late joiner");
    }

    [Fact]
    public async Task Broken_stream_shows_reconnecting_then_resumes_from_the_last_seq()
    {
        var seen = new Recorder();
        using var watch = _hub.Watch(AuctionId, seen.On);
        await Wait.UntilAsync(() => _source.Calls.Count == 1, "the stream to open");
        _source.Push(Snapshot(5));
        _source.Push(Bid(6, 20m, Bob, "b***42"));
        await Wait.UntilAsync(() => watch.Current.View.Seq == 6, "seq 6");

        _source.Break();
        await Wait.UntilAsync(() => watch.Current.Status == LiveFeedStatus.Reconnecting, "Reconnecting");
        _source.Calls.Should().HaveCount(1, "it waits 0.2 s before trying again");

        await Task.Delay(50);   // let the feed start its wait before the fake clock moves
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        await Wait.UntilAsync(() => _source.Calls.Count == 2, "the second connect");

        _source.Latest.LastSeenSeq.Should().Be(6, "it resumes from the last seq it had");
        watch.Current.Status.Should().Be(LiveFeedStatus.Reconnecting, "still catching up");

        _source.Push(Bid(7, 21m, Alice, "a***2x"));
        _source.Push(Heartbeat(7));   // the Api's "caught up" mark
        await Wait.UntilAsync(() => watch.Current.Status == LiveFeedStatus.Live, "Live again");
        watch.Current.View.Seq.Should().Be(7);
        watch.Current.View.Price.Should().Be(21m);
    }

    [Fact]
    public async Task Backoff_doubles_after_each_failure()
    {
        using var watch = _hub.Watch(AuctionId, _ => { });
        await Wait.UntilAsync(() => _source.Calls.Count == 1, "the stream to open");

        _source.Break();
        await Wait.UntilAsync(() => watch.Current.Status == LiveFeedStatus.Reconnecting, "Reconnecting");
        await Task.Delay(50);
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        await Wait.UntilAsync(() => _source.Calls.Count == 2, "try 2");

        _source.Break();
        await Task.Delay(50);
        _clock.Advance(TimeSpan.FromMilliseconds(399));
        await Task.Delay(50);
        _source.Calls.Should().HaveCount(2, "the second wait is 0.4 s");
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await Wait.UntilAsync(() => _source.Calls.Count == 3, "try 3");
    }

    [Theory]
    [InlineData(1, 200)]
    [InlineData(2, 400)]
    [InlineData(5, 3200)]
    [InlineData(6, 5000)]
    [InlineData(50, 5000)]
    public void Backoff_is_0_2_s_doubling_to_5_s(int failures, int ms)
    {
        LiveHubOptions.Backoff(failures).Should().Be(TimeSpan.FromMilliseconds(ms));
    }

    [Fact]
    public async Task Stream_closes_30_s_after_the_last_watcher_leaves()
    {
        var watch = _hub.Watch(AuctionId, _ => { });
        await Wait.UntilAsync(() => _source.Calls.Count == 1, "the stream to open");
        var token = _source.Latest.Token;

        watch.Dispose();
        _clock.Advance(TimeSpan.FromSeconds(29));
        token.IsCancellationRequested.Should().BeFalse();
        _hub.OpenStreams.Should().Be(1);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => token.IsCancellationRequested, "the stream to close");
        _hub.OpenStreams.Should().Be(0);
    }

    [Fact]
    public async Task Coming_back_within_30_s_keeps_the_same_stream()
    {
        var first = _hub.Watch(AuctionId, _ => { });
        await Wait.UntilAsync(() => _source.Calls.Count == 1, "the stream to open");
        var token = _source.Latest.Token;
        first.Dispose();
        _clock.Advance(TimeSpan.FromSeconds(20));

        using var again = _hub.Watch(AuctionId, _ => { });
        _clock.Advance(TimeSpan.FromSeconds(20));

        token.IsCancellationRequested.Should().BeFalse();
        _source.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task Different_auctions_get_their_own_streams()
    {
        using var a = _hub.Watch(AuctionId, _ => { });
        using var b = _hub.Watch(Guid.NewGuid(), _ => { });

        await Wait.UntilAsync(() => _source.Calls.Count == 2, "two streams");
        _hub.OpenStreams.Should().Be(2);
    }

    [Fact]
    public async Task A_watcher_that_throws_does_not_stop_the_others()
    {
        var good = new Recorder();
        using var bad = _hub.Watch(AuctionId, _ => throw new InvalidOperationException("boom"));
        using var ok = _hub.Watch(AuctionId, good.On);
        await Wait.UntilAsync(() => _source.Calls.Count == 1, "the stream to open");

        _source.Push(Snapshot(3));

        await Wait.UntilAsync(() => good.Last?.View.Seq == 3, "the good watcher");
    }
}
