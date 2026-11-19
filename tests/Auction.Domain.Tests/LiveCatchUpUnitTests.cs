using Auction.Infrastructure.Live;
using Auction.Infrastructure.Redis;
using FluentAssertions;
using Google.Protobuf;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 04 task 4 without Redis: snapshot or replay, no gap or repeat, stored event mapping.</summary>
public class LiveCatchUpUnitTests
{
    private static readonly Guid Auction = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // ---------- snapshot or replay (R1.1 to R1.3) ----------

    [Fact]
    public void Last_seen_0_gets_a_snapshot()
    {
        LiveSequencer.ChooseStart(lastSeen: 0, currentSeq: 10, oldestKeptSeq: 1).Should().Be(StartKind.Snapshot);
    }

    [Fact]
    public void Missed_events_still_kept_are_replayed()
    {
        LiveSequencer.ChooseStart(lastSeen: 7, currentSeq: 10, oldestKeptSeq: 1).Should().Be(StartKind.Replay);
        LiveSequencer.ChooseStart(lastSeen: 7, currentSeq: 10, oldestKeptSeq: 8).Should().Be(StartKind.Replay, "8 is the first one missed");
    }

    [Fact]
    public void Very_old_last_seen_gets_a_snapshot()
    {
        LiveSequencer.ChooseStart(lastSeen: 7, currentSeq: 500, oldestKeptSeq: 301).Should().Be(StartKind.Snapshot);
        LiveSequencer.ChooseStart(lastSeen: 7, currentSeq: 10, oldestKeptSeq: 9).Should().Be(StartKind.Snapshot, "8 is gone");
    }

    [Fact]
    public void Up_to_date_client_goes_straight_to_live()
    {
        LiveSequencer.ChooseStart(lastSeen: 10, currentSeq: 10, oldestKeptSeq: null).Should().Be(StartKind.Replay);
    }

    [Fact]
    public void Client_ahead_of_redis_or_auction_not_in_redis_gets_a_snapshot()
    {
        LiveSequencer.ChooseStart(lastSeen: 12, currentSeq: 10, oldestKeptSeq: 1).Should().Be(StartKind.Snapshot);
        LiveSequencer.ChooseStart(lastSeen: 5, currentSeq: null, oldestKeptSeq: null).Should().Be(StartKind.Snapshot);
        LiveSequencer.ChooseStart(lastSeen: 5, currentSeq: 10, oldestKeptSeq: null).Should().Be(StartKind.Snapshot, "nothing kept");
    }

    // ---------- live loop ----------

    [Theory]
    [InlineData(5, 6, LiveStep.Send)]
    [InlineData(5, 5, LiveStep.Skip)]
    [InlineData(5, 3, LiveStep.Skip)]
    [InlineData(5, 7, LiveStep.Resync)]
    [InlineData(0, 1, LiveStep.Send)]
    public void Next_event_rules(long sent, long seq, LiveStep step)
    {
        LiveSequencer.Next(sent, seq).Should().Be(step);
    }

    [Fact]
    public void Overlap_between_catch_up_and_live_is_sent_once()
    {
        // Catch-up sent up to 8; the live queue still holds 7 and 8 from before, then 9 and 10.
        var sent = 8L;
        var output = new List<long>();
        foreach (var seq in new long[] { 7, 8, 9, 10 })
        {
            if (LiveSequencer.Next(sent, seq) == LiveStep.Send)
            {
                output.Add(seq);
                sent = seq;
            }
        }

        output.Should().Equal(9, 10);
    }

    [Theory]
    [InlineData(4, new long[] { 5, 6, 7 }, true)]
    [InlineData(4, new long[] { }, true)]
    [InlineData(4, new long[] { 6, 7 }, false)]
    [InlineData(4, new long[] { 5, 7 }, false)]
    [InlineData(4, new long[] { 5, 5 }, false)]
    public void Replay_must_be_unbroken(long lastSeen, long[] seqs, bool ok)
    {
        LiveSequencer.IsUnbroken(lastSeen, seqs).Should().Be(ok);
    }

    // ---------- stored events (the events ZSET) ----------

    private static string BidJson() =>
        $"{{\"seq\":3,\"type\":\"Bid\",\"priceC\":3100,\"leader\":\"{Alice}\",\"bidCount\":3,\"endMs\":5000,\"extended\":true," +
        $"\"reserveMet\":\"1\",\"minNextC\":3200,\"atMs\":777," +
        $"\"rows\":[{{\"seq\":3,\"user\":\"{Bob}\",\"amountC\":3000,\"atMs\":777,\"kind\":\"AutoOutbid\"}}," +
        $"{{\"seq\":3,\"user\":\"{Alice}\",\"amountC\":3100,\"atMs\":777,\"kind\":\"ProxyAuto\"}}]}}";

    private static readonly Dictionary<Guid, string> Handles = new() { [Alice] = "a***2x", [Bob] = "b***42" };

    [Fact]
    public void Stored_bid_becomes_bid_accepted()
    {
        var stored = StoredEventMapper.Parse(BidJson())!;

        var live = StoredEventMapper.Map(Auction, stored, "USD", Handles);

        live.Seq.Should().Be(3);
        live.ServerTsMs.Should().Be(777);
        live.BidAccepted.Price.Units.Should().Be(31);
        live.BidAccepted.MinNextBid.Units.Should().Be(32);
        live.BidAccepted.ReserveMet.Should().BeTrue();
        live.BidAccepted.Extended.Should().BeTrue();
        live.BidAccepted.Leader.Handle.Should().Be("a***2x");
        live.BidAccepted.Rows.Select(r => r.Bidder.Handle).Should().Equal("b***42", "a***2x");
        StoredEventMapper.UsersIn(stored).Distinct().Should().BeEquivalentTo([Alice, Bob]);
    }

    [Fact]
    public void Stored_freeze_becomes_state_changed()
    {
        var stored = StoredEventMapper.Parse("{\"seq\":9,\"type\":\"State\",\"status\":\"Frozen\",\"atMs\":50}")!;

        var live = StoredEventMapper.Map(Auction, stored, "USD", Handles);

        live.Seq.Should().Be(9);
        live.StateChanged.Status.Should().Be("Frozen");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"seq\":0,\"type\":\"Bid\"}")]
    [InlineData("{\"seq\":3,\"type\":\"Mystery\"}")]
    public void Unreadable_stored_events_are_null(string json)
    {
        StoredEventMapper.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Mapped_stored_event_has_no_max()
    {
        var live = StoredEventMapper.Map(Auction, StoredEventMapper.Parse(BidJson())!, "USD", Handles);

        JsonFormatter.Default.Format(live).Should().NotContainEquivalentOf("max");
    }

    [Fact]
    public void Bid_lua_stores_the_public_fields_catch_up_needs()
    {
        var script = LuaScripts.Load("bid.lua");
        var eventJson = script[script.IndexOf("local event = ", StringComparison.Ordinal)..script.IndexOf("redis.call('ZADD', KEYS[4]", StringComparison.Ordinal)];

        eventJson.Should().Contain("reserveMet").And.Contain("minNextC").And.Contain("atMs").And.Contain("rows");
        eventJson.Should().NotContain("leaderMax").And.NotContain("maxC");
    }
}
