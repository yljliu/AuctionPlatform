using Auction.Contracts;
using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Live;
using Auction.Infrastructure.Redis;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Xunit;
using ProtoAuctionEvent = Auction.Contracts.AuctionEvent;

namespace Auction.Domain.Tests;

/// <summary>Spec 04 tasks 1 to 3 without Redis or SQL: the contract, entry mapping, the bus, snapshots.</summary>
public class LiveUnitTests
{
    private static readonly Guid Auction = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static readonly Dictionary<Guid, string> Handles = new() { [Alice] = "a***2x", [Bob] = "b***42" };

    // ---------- task 1: live.proto ----------

    [Fact]
    public void Live_service_has_subscribe_and_snapshot()
    {
        LiveService.Descriptor.Methods.Select(m => m.Name).Should().BeEquivalentTo("SubscribeAuction", "GetAuctionSnapshot");
        LiveService.Descriptor.FindMethodByName("SubscribeAuction").IsServerStreaming.Should().BeTrue();
    }

    [Fact]
    public void Event_body_is_snapshot_bid_state_or_heartbeat()
    {
        ProtoAuctionEvent.Descriptor.Oneofs.Single().Fields.Select(f => f.Name)
            .Should().Equal("snapshot", "bid_accepted", "state_changed", "heartbeat");
        ProtoAuctionEvent.Descriptor.FindFieldByName("seq").Should().NotBeNull();
        ProtoAuctionEvent.Descriptor.FindFieldByName("server_ts_ms").Should().NotBeNull();
    }

    [Fact]
    public void No_live_message_can_carry_a_max_or_the_reserve_amount()
    {
        var names = FieldNamesDeep(ProtoAuctionEvent.Descriptor);

        names.Should().NotContain(n => n.Contains("max"));
        names.Where(n => n.Contains("reserve")).Should().OnlyContain(n => n == "reserve_met");
    }

    [Fact]
    public void Bidder_carries_a_user_id_so_the_web_can_mark_you()
    {
        PublicBidRow.Descriptor.FindFieldByName("bidder").MessageType.FindFieldByName("user_id").Should().NotBeNull();
    }

    // ---------- task 2: entry to event ----------

    /// <summary>A bid.lua stream entry with private values 77777.00 (max) and 99999.00 (leader max).</summary>
    private static Dictionary<string, string> BidFields(string result = "OutbidByProxy", string seq = "3", string reserveMet = "0") => new()
    {
        ["type"] = "Bid", ["auction"] = Auction.ToString(), ["eseq"] = "4", ["seq"] = seq, ["user"] = Bob.ToString(),
        ["maxC"] = "7777700", ["result"] = result, ["priceC"] = "3100", ["leader"] = Alice.ToString(), ["leaderMaxC"] = "9999900",
        ["bidCount"] = "3", ["endMs"] = "1763400000000", ["extended"] = "1", ["reserveMet"] = reserveMet, ["minNextC"] = "3200",
        ["cur"] = "USD", ["at"] = "1763300000123", ["idem"] = Guid.NewGuid().ToString(), ["outbid"] = "", ["corr"] = "c",
        ["rows"] = $"[{{\"seq\":3,\"user\":\"{Bob}\",\"amountC\":3000,\"atMs\":1,\"kind\":\"AutoOutbid\"}}," +
                   $"{{\"seq\":3,\"user\":\"{Alice}\",\"amountC\":3100,\"atMs\":1,\"kind\":\"ProxyAuto\"}}]",
    };

    private static ProtoAuctionEvent? Map(Dictionary<string, string> fields) =>
        LiveEventMapper.Map(PersistEntryParser.Parse("1-0", fields), fields, Handles);

    [Fact]
    public void Bid_entry_becomes_bid_accepted()
    {
        var live = Map(BidFields())!;

        live.Seq.Should().Be(3);
        live.ServerTsMs.Should().Be(1763300000123);
        live.AuctionId.Should().Be(Auction.ToString());
        var bid = live.BidAccepted;
        bid.Price.Units.Should().Be(31);
        bid.Leader.Handle.Should().Be("a***2x");
        bid.Leader.UserId.Should().Be(Alice.ToString());
        bid.BidCount.Should().Be(3);
        bid.ReserveMet.Should().BeFalse();
        bid.Extended.Should().BeTrue();
        bid.EndTimeMs.Should().Be(1763400000000);
        bid.MinNextBid.Units.Should().Be(32);
        bid.Rows.Select(r => (r.Bidder.Handle, r.Amount.Units, r.Auto)).Should().Equal(("b***42", 30L, true), ("a***2x", 31L, true));
    }

    [Fact]
    public void Live_event_never_carries_the_max_or_leader_max()
    {
        var json = JsonFormatter.Default.Format(Map(BidFields())!);

        json.Should().NotContain("77777").And.NotContain("99999").And.NotContainEquivalentOf("max");
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Reserve_met_is_only_yes_no_or_empty(string reserveMet, bool? expected)
    {
        Map(BidFields(reserveMet: reserveMet))!.BidAccepted.ReserveMet.Should().Be(expected);
    }

    [Fact]
    public void Max_raise_is_not_published()
    {
        var fields = BidFields(result: "MaxRaised", seq: "-");
        fields["rows"] = $"[{{\"seq\":0,\"user\":\"{Alice}\",\"amountC\":3100,\"atMs\":1,\"kind\":\"MaxRaise\"}}]";

        Map(fields).Should().BeNull();
    }

    [Fact]
    public void State_entry_becomes_state_changed()
    {
        var fields = new Dictionary<string, string>
        {
            ["type"] = "State", ["auction"] = Auction.ToString(), ["status"] = "Settled", ["seq"] = "9", ["eseq"] = "12",
            ["at"] = "50", ["outcome"] = "Sold", ["finalC"] = "4500", ["cur"] = "EUR",
        };

        var live = Map(fields)!;

        live.Seq.Should().Be(9);
        live.StateChanged.Status.Should().Be("Settled");
        live.StateChanged.Outcome.Should().Be("Sold");
        live.StateChanged.FinalPrice.Units.Should().Be(45);
        live.StateChanged.FinalPrice.Currency.Should().Be("EUR");
    }

    [Fact]
    public void Unknown_bidder_gets_a_safe_handle()
    {
        var live = LiveEventMapper.Map(PersistEntryParser.Parse("1-0", BidFields()), BidFields(), new Dictionary<Guid, string>())!;

        live.BidAccepted.Rows.Should().OnlyContain(r => r.Bidder.Handle == "***");
    }

    [Fact]
    public void Users_in_lists_leader_and_row_bidders()
    {
        LiveEventMapper.UsersIn(PersistEntryParser.Parse("1-0", BidFields())).Distinct().Should().BeEquivalentTo([Alice, Bob]);
    }

    // ---------- task 2: the bus ----------

    private static ProtoAuctionEvent Event(long seq) => new() { Seq = seq, Heartbeat = new Heartbeat() };

    [Fact]
    public void Subscriber_gets_events_of_its_auction_only()
    {
        var bus = new AuctionEventBus();
        using var mine = bus.Subscribe(Auction);
        using var other = bus.Subscribe(Guid.NewGuid());

        bus.Publish(Auction, Event(1)).Should().Be(1);

        mine.Reader.TryRead(out var got).Should().BeTrue();
        got!.Seq.Should().Be(1);
        other.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void Every_subscriber_of_an_auction_gets_each_event()
    {
        var bus = new AuctionEventBus();
        using var a = bus.Subscribe(Auction);
        using var b = bus.Subscribe(Auction);

        bus.Publish(Auction, Event(7)).Should().Be(2);
        bus.SubscriberCount.Should().Be(2);
    }

    [Fact]
    public void Disposed_subscriber_is_removed()
    {
        var bus = new AuctionEventBus();
        var sub = bus.Subscribe(Auction);

        sub.Dispose();
        sub.Dispose();   // twice is fine

        bus.SubscriberCount.Should().Be(0);
        bus.Publish(Auction, Event(1)).Should().Be(0);
    }

    [Fact]
    public void Full_queue_drops_the_oldest_and_marks_lagged()
    {
        var bus = new AuctionEventBus();
        using var sub = bus.Subscribe(Auction);

        for (var seq = 1; seq <= AuctionSubscription.Capacity + 5; seq++)
        {
            bus.Publish(Auction, Event(seq));
        }

        sub.TakeLagged().Should().BeTrue();
        sub.TakeLagged().Should().BeFalse("reading the flag clears it");
        sub.Reader.TryRead(out var first).Should().BeTrue();
        first!.Seq.Should().Be(6, "the 5 oldest were dropped");
    }

    [Fact]
    public void Slow_reader_never_blocks_publishing()
    {
        var bus = new AuctionEventBus();
        using var sub = bus.Subscribe(Auction);

        var act = () =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                bus.Publish(Auction, Event(i));
            }
        };

        act.ExecutionTime().Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    // ---------- task 3: snapshot ----------

    private static SnapshotInput Input(long? reserveC = 77777, int bidCount = 3, long priceC = 3100) => new(
        Auction, "Live", Seq: 3, PriceC: priceC, StartC: 1000, BidCount: bidCount, Leader: bidCount > 0 ? Alice : null,
        EndMs: 1763400000000, ExtUsedMs: 120_000, ReserveC: reserveC, Inc: "0:100,5000:250", Currency: "USD", Watchers: 4,
        Bids:
        [
            new PublicBidJson(3, Alice, 3100, 30, "ProxyAuto"),
            new PublicBidJson(3, Bob, 3000, 30, "AutoOutbid"),
            new PublicBidJson(1, Alice, 1000, 10, "Manual"),
        ],
        ServerTsMs: 999);

    [Fact]
    public void Snapshot_carries_the_live_picture()
    {
        var snap = SnapshotMapper.Map(Input(), Handles);

        snap.Seq.Should().Be(3);
        snap.Status.Should().Be("Live");
        snap.CurrentPrice.Units.Should().Be(31);
        snap.MinNextBid.Units.Should().Be(32);
        snap.BidCount.Should().Be(3);
        snap.Leader.Handle.Should().Be("a***2x");
        snap.ExtensionUsedSec.Should().Be(120);
        snap.Watchers.Should().Be(4);
        snap.RecentBids.Select(r => r.Bidder.Handle).Should().Equal("a***2x", "b***42", "a***2x");
        snap.RecentBids.Select(r => r.Auto).Should().Equal(true, true, false);
        snap.IncrementTiers.Should().HaveCount(2);
        snap.ServerTsMs.Should().Be(999);
    }

    [Fact]
    public void Snapshot_has_no_reserve_or_max_values()
    {
        var json = JsonFormatter.Default.Format(SnapshotMapper.Map(Input(reserveC: 777777), Handles));

        json.Should().NotContain("7777").And.NotContainEquivalentOf("max").And.NotContain("reservePrice");
        FieldNamesDeep(AuctionSnapshot.Descriptor).Where(n => n.Contains("reserve")).Should().OnlyContain(n => n == "reserve_met");
    }

    [Theory]
    [InlineData(null, 3, 3100, null)]     // no reserve
    [InlineData(3000L, 3, 3100, true)]    // met
    [InlineData(5000L, 3, 3100, false)]   // not met
    [InlineData(1000L, 0, 0, false)]      // no bids yet is never met
    public void Reserve_met_rules(long? reserveC, int bidCount, long priceC, bool? expected)
    {
        SnapshotMapper.Map(Input(reserveC, bidCount, priceC), Handles).ReserveMet.Should().Be(expected);
    }

    [Fact]
    public void No_bids_shows_the_starting_price_and_no_leader()
    {
        var snap = SnapshotMapper.Map(Input(bidCount: 0, priceC: 0) with { Bids = [] }, Handles);

        snap.CurrentPrice.Units.Should().Be(10);
        snap.MinNextBid.Units.Should().Be(10, "the first bid only has to meet the start");
        snap.Leader.Should().BeNull();
    }

    [Fact]
    public void Handle_key_is_h_and_the_user_id()
    {
        RedisKeys.Handle(Alice).Should().Be($"h:{Alice}");
    }

    private static List<string> FieldNamesDeep(MessageDescriptor root)
    {
        var names = new List<string>();
        var seen = new HashSet<string>();
        var todo = new Stack<MessageDescriptor>();
        todo.Push(root);
        while (todo.Count > 0)
        {
            var message = todo.Pop();
            if (!seen.Add(message.FullName))
            {
                continue;
            }

            foreach (var field in message.Fields.InDeclarationOrder())
            {
                names.Add(field.Name);
                if (field.FieldType == FieldType.Message)
                {
                    todo.Push(field.MessageType);
                }
            }
        }

        return names;
    }
}
