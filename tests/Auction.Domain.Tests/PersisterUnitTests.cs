using System.Text.Json;
using System.Text.RegularExpressions;
using Auction.Domain.Enums;
using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Redis;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 03 tasks 8 and 9 without Redis or SQL: reading entries, planning SQL rows, stream math.</summary>
public class PersisterUnitTests
{
    private static readonly Guid AuctionA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid AuctionB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    /// <summary>The fields bid.lua writes with XADD.</summary>
    private static Dictionary<string, string> BidFields(Guid auction, long eseq, string seq, Guid user, long maxC, string result,
        long priceC, Guid leader, int bidCount, string rowsJson, Guid? idem = null) => new()
    {
        ["type"] = "Bid", ["auction"] = auction.ToString(), ["eseq"] = eseq.ToString(), ["seq"] = seq,
        ["user"] = user.ToString(), ["maxC"] = maxC.ToString(), ["result"] = result, ["priceC"] = priceC.ToString(),
        ["leader"] = leader.ToString(), ["leaderMaxC"] = "99999", ["bidCount"] = bidCount.ToString(),
        ["endMs"] = "1763400000000", ["extended"] = "0", ["rows"] = rowsJson, ["outbid"] = "",
        ["idem"] = (idem ?? Guid.NewGuid()).ToString(), ["at"] = "1763300000000", ["corr"] = "c",
    };

    private static string Rows(params (Guid User, long AmountC, string Kind)[] rows) =>
        "[" + string.Join(",", rows.Select(r => $"{{\"seq\":1,\"user\":\"{r.User}\",\"amountC\":{r.AmountC},\"atMs\":1,\"kind\":\"{r.Kind}\"}}")) + "]";

    private static PersistEntry Entry(string id, Dictionary<string, string> fields) => PersistEntryParser.Parse(id, fields);

    // ---------- PersistEntryParser ----------

    [Fact]
    public void Reads_a_bid_entry()
    {
        var idem = Guid.NewGuid();
        var entry = Entry("1-0", BidFields(AuctionA, 3, "2", Bob, 5000, "OutbidByProxy", 1200, Alice, 2,
            Rows((Bob, 1100, "AutoOutbid"), (Alice, 1200, "ProxyAuto")), idem));

        entry.Type.Should().Be(PersistEntryType.Bid);
        entry.AuctionId.Should().Be(AuctionA);
        entry.ESeq.Should().Be(3);
        entry.Seq.Should().Be(2);
        entry.User.Should().Be(Bob);
        entry.MaxC.Should().Be(5000);
        entry.PriceC.Should().Be(1200);
        entry.Leader.Should().Be(Alice);
        entry.BidCount.Should().Be(2);
        entry.IdempotencyKey.Should().Be(idem);
        entry.Rows.Should().Equal(new PersistRow(Bob, 1100, BidKind.AutoOutbid), new PersistRow(Alice, 1200, BidKind.ProxyAuto));
    }

    [Fact]
    public void Max_raise_entry_has_no_seq()
    {
        var entry = Entry("1-0", BidFields(AuctionA, 4, "-", Alice, 9000, "MaxRaised", 1200, Alice, 2, Rows((Alice, 1200, "MaxRaise"))));

        entry.Seq.Should().BeNull();
    }

    [Fact]
    public void Reads_a_state_entry()
    {
        var entry = Entry("1-0", new()
        {
            ["type"] = "State", ["auction"] = AuctionA.ToString(), ["status"] = "Frozen", ["seq"] = "5", ["eseq"] = "6", ["at"] = "100",
        });

        entry.Type.Should().Be(PersistEntryType.State);
        entry.Status.Should().Be("Frozen");
        entry.Seq.Should().Be(5);
        entry.ESeq.Should().Be(6);
    }

    public static TheoryData<string, string?> BrokenFields => new()
    {
        { "type", "Mystery" },
        { "auction", "not-a-guid" },
        { "eseq", "0" },
        { "eseq", "x" },
        { "rows", "not json" },
        { "rows", "[]" },
        { "result", "Winning" },
        { "user", null },
        { "priceC", "1.5" },
    };

    [Theory]
    [MemberData(nameof(BrokenFields))]
    public void Broken_entries_are_format_errors(string field, string? value)
    {
        var fields = BidFields(AuctionA, 1, "1", Alice, 1000, "Leading", 1000, Alice, 1, Rows((Alice, 1000, "Manual")));
        if (value == null)
        {
            fields.Remove(field);
        }
        else
        {
            fields[field] = value;
        }

        var act = () => PersistEntryParser.Parse("9-0", fields);

        act.Should().Throw<FormatException>().WithMessage("*9-0*");
    }

    [Fact]
    public void Price_change_without_seq_is_a_format_error()
    {
        var act = () => Entry("1-0", BidFields(AuctionA, 1, "-", Alice, 1000, "Leading", 1000, Alice, 1, Rows((Alice, 1000, "Manual"))));

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Every_field_the_parser_needs_is_written_by_bid_lua()
    {
        var script = LuaScripts.Load("bid.lua");
        var xadd = script[script.IndexOf("redis.call('XADD'", StringComparison.Ordinal)..];
        var written = Regex.Matches(xadd, @"'([a-zA-Z]+)',").Select(m => m.Groups[1].Value).ToHashSet();

        written.Should().Contain(["type", "auction", "eseq", "seq", "user", "maxC", "result", "priceC", "leader", "bidCount",
            "endMs", "extended", "rows", "idem", "at"]);
    }

    [Fact]
    public void Freeze_lua_writes_a_state_entry_the_parser_can_read()
    {
        var script = LuaScripts.Load("freeze.lua");

        script.Should().Contain("'type', 'State'").And.Contain("'status', 'Frozen'").And.Contain("'eseq'").And.Contain("'at'");
        LuaScripts.Load("persisted.lua").Should().Contain("persistedEseq");
    }

    // ---------- PersistPlanner ----------

    [Fact]
    public void Rows_get_their_position_and_only_the_bidders_row_keeps_the_max_and_key()
    {
        var idem = Guid.NewGuid();
        var entry = Entry("1-0", BidFields(AuctionA, 7, "4", Bob, 30000, "Leading", 20250, Bob, 4,
            Rows((Alice, 20000, "ProxyAuto"), (Bob, 20250, "Manual")), idem));

        var plan = PersistPlanner.Build([entry]);

        plan.Bids.Should().HaveCount(2);
        plan.Bids[0].Should().Match<BidInsert>(b => b.UserId == Alice && b.Idx == 0 && b.MaxAmount == null && b.IdempotencyKey == null
                                                   && b.Amount == 200m && b.Kind == BidKind.ProxyAuto && b.ESeq == 7 && b.Seq == 4);
        plan.Bids[1].Should().Match<BidInsert>(b => b.UserId == Bob && b.Idx == 1 && b.MaxAmount == 300m && b.IdempotencyKey == idem
                                                   && b.Amount == 202.5m);
    }

    [Fact]
    public void Tie_rows_give_the_max_to_the_first_row_of_the_bidder_only()
    {
        var entry = Entry("1-0", BidFields(AuctionA, 2, "2", Bob, 5000, "OutbidByProxy", 5000, Alice, 2,
            Rows((Bob, 5000, "AutoOutbid"), (Alice, 5000, "ProxyAuto"))));

        var plan = PersistPlanner.Build([entry]);

        plan.Bids.Select(b => b.MaxAmount).Should().Equal(50m, null);
    }

    [Fact]
    public void Each_price_change_becomes_one_public_event_without_a_max()
    {
        var bid = Entry("1-0", BidFields(AuctionA, 1, "1", Alice, 77777, "Leading", 1000, Alice, 1, Rows((Alice, 1000, "Manual"))));
        var raise = Entry("2-0", BidFields(AuctionA, 2, "-", Alice, 88888, "MaxRaised", 1000, Alice, 1, Rows((Alice, 1000, "MaxRaise"))));

        var plan = PersistPlanner.Build([bid, raise]);

        plan.Events.Should().ContainSingle();
        var payload = plan.Events[0].Payload;
        JsonDocument.Parse(payload).RootElement.GetProperty("type").GetString().Should().Be("Bid");
        payload.Should().NotContain("77777").And.NotContain("88888").And.NotContainEquivalentOf("max");
        plan.Events[0].EventType.Should().Be(PersistPlanner.BidEvent);
    }

    [Fact]
    public void Last_entry_of_each_auction_sets_its_state_and_entries_are_grouped_in_order()
    {
        var entries = new[]
        {
            Entry("1-0", BidFields(AuctionA, 1, "1", Alice, 5000, "Leading", 1000, Alice, 1, Rows((Alice, 1000, "Manual")))),
            Entry("2-0", BidFields(AuctionB, 1, "1", Bob, 3000, "Leading", 500, Bob, 1, Rows((Bob, 500, "Manual")))),
            Entry("3-0", BidFields(AuctionA, 2, "2", Bob, 9000, "Leading", 5250, Bob, 2, Rows((Alice, 5000, "ProxyAuto"), (Bob, 5250, "Manual")))),
            Entry("4-0", BidFields(AuctionA, 3, "-", Bob, 9999, "MaxRaised", 5250, Bob, 2, Rows((Bob, 5250, "MaxRaise")))),
        };

        var plan = PersistPlanner.Build(entries);

        var a = plan.Auctions.Single(u => u.AuctionId == AuctionA);
        a.LastESeq.Should().Be(3);
        a.LastSeq.Should().Be(2, "the max raise has no seq");
        a.CurrentPrice.Should().Be(52.5m);
        a.CurrentLeaderId.Should().Be(Bob);
        a.BidCount.Should().Be(2);
        a.HasBid.Should().BeTrue();

        plan.Auctions.Single(u => u.AuctionId == AuctionB).CurrentPrice.Should().Be(5m);
        plan.Bids.Where(b => b.AuctionId == AuctionA).Select(b => (b.ESeq, b.Idx)).Should().Equal((1L, (byte)0), (2L, (byte)0), (2L, (byte)1), (3L, (byte)0));
    }

    [Fact]
    public void State_only_batch_moves_the_counters_but_not_the_price()
    {
        var state = Entry("1-0", new()
        {
            ["type"] = "State", ["auction"] = AuctionA.ToString(), ["status"] = "Frozen", ["seq"] = "9", ["eseq"] = "12", ["at"] = "100",
        });

        var plan = PersistPlanner.Build([state]);

        plan.Bids.Should().BeEmpty();
        plan.Events.Single().EventType.Should().Be(PersistPlanner.StateEvent);
        var update = plan.Auctions.Single();
        update.HasBid.Should().BeFalse();
        update.CurrentPrice.Should().BeNull();
        update.LastESeq.Should().Be(12);
        update.LastSeq.Should().Be(9);
    }

    // ---------- stream ids, trim, lag, backoff ----------

    [Theory]
    [InlineData("1763400000000-0", 1763400000000, 0)]
    [InlineData("5-12", 5, 12)]
    public void Stream_ids_parse(string text, long ms, long seq)
    {
        StreamId.Parse(text).Should().Be(new StreamId(ms, seq));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("5-")]
    [InlineData("5-x")]
    public void Bad_stream_ids_fail(string text)
    {
        var act = () => StreamId.Parse(text);
        act.Should().Throw<FormatException>();
        StreamId.TryParse(text).Should().BeNull();
    }

    [Fact]
    public void Stream_ids_order_by_ms_then_seq()
    {
        (StreamId.Parse("5-9") < StreamId.Parse("6-0")).Should().BeTrue();
        (StreamId.Parse("6-1") > StreamId.Parse("6-0")).Should().BeTrue();
    }

    private const long Now = 10_000_000;

    [Fact]
    public void Trim_keeps_what_the_slowest_group_still_needs()
    {
        var minId = StreamMath.TrimMinId(
        [
            new GroupPosition("persist", LowestPendingId: "8000000-3", LastDeliveredId: "9000000-0"),
            new GroupPosition("notify", LowestPendingId: null, LastDeliveredId: "7000000-0"),
        ], Now);

        minId.Should().Be("7000000-0");
    }

    [Fact]
    public void Trim_never_removes_the_last_10_minutes()
    {
        var minId = StreamMath.TrimMinId([new GroupPosition("persist", null, "9999999-0")], Now);

        minId.Should().Be($"{Now - 600_000}-0");
    }

    [Fact]
    public void Trim_is_skipped_with_no_groups_or_a_group_that_read_nothing()
    {
        StreamMath.TrimMinId([], Now).Should().BeNull();
        StreamMath.TrimMinId([new GroupPosition("persist", null, null)], Now).Should().BeNull();
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData("9000000-0", null, 1_000_000)]
    [InlineData(null, "9500000-0", 500_000)]
    [InlineData("9500000-0", "9000000-5", 1_000_000)]   // the older one counts
    [InlineData("10000005-0", null, 0)]                 // clock a little behind: never negative
    public void Lag_is_the_age_of_the_oldest_unwritten_entry(string? pending, string? undelivered, long expected)
    {
        StreamMath.LagMs(Now, pending, undelivered).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 100)]
    [InlineData(2, 200)]
    [InlineData(3, 400)]
    [InlineData(6, 3200)]
    [InlineData(7, 5000)]
    [InlineData(1000, 5000)]
    public void Backoff_doubles_up_to_5_seconds(int failures, int expectedMs)
    {
        StreamMath.Backoff(failures).Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }
}
