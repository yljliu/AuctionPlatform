using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Simulator.Sim;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 08 task 6: each verifier check (R4.1 to R4.7) catches exactly its own fault.</summary>
public class SimulatorVerifierTests
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid K1 = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid K2 = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly DateTime Scheduled = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Alice bids max 20 on a 10.00 start; Bob bids max 15 and loses to Alice's proxy: price 16.00, Alice leads.</summary>
    private static AuctionData Good(Action<List<BidRowData>>? bids = null, Action<List<EventData>>? events = null,
        AuctionStatus status = AuctionStatus.Settled, decimal price = 16m, DateTime? end = null)
    {
        var b = new List<BidRowData>
        {
            new(1, 0, BidKind.Manual, Alice, 10m, 20m, K1, false),
            new(2, 0, BidKind.AutoOutbid, Bob, 15m, 15m, K2, false),
            new(2, 1, BidKind.ProxyAuto, Alice, 16m, null, null, false),
        };
        var e = new List<EventData> { new(1, "Bid", "{\"priceC\":1000}"), new(2, "Bid", "{\"priceC\":1600}") };
        bids?.Invoke(b);
        events?.Invoke(e);
        return new AuctionData(A, status, price, Alice, end ?? Scheduled, Scheduled, 600, 10m, IncrementTable.Parse("0:100"), b, e);
    }

    private static readonly LedgerEntry[] Ledger = [new(A, K1, 1), new(A, K2, 2)];
    private static readonly Dictionary<Guid, long> Seen = new() { [A] = 2 };

    private static VerifyReport Check(AuctionData data, IReadOnlyDictionary<Guid, long>? seen = null) =>
        VerifierChecks.Check([data], Ledger, seen ?? Seen, bidsSent: 5);

    [Fact]
    public void A_good_auction_passes()
    {
        var report = Check(Good());

        report.Result.Should().Be("PASS");
        report.Passed.Should().BeTrue();
        report.Auctions.Should().Be(1);
        report.BidsSent.Should().Be(5);
        report.Accepted.Should().Be(2);
        report.FailingAuctions.Should().BeEmpty();
        report.ToJson().Should().Contain("\"result\": \"PASS\"").And.Contain("\"missingInSql\": 0").And.Contain("\"failingAuctions\": []");
    }

    [Fact]
    public void R4_1_a_missing_bid_row_fails_and_names_the_auction()
    {
        var report = Check(Good(b => b.RemoveAll(r => r.IdempotencyKey == K2)));

        report.Result.Should().Be("FAIL");
        report.MissingInSql.Should().Be(1);
        report.FailingAuctions.Should().Equal(A.ToString());
    }

    [Fact]
    public void R4_1_the_same_key_twice_is_a_duplicate()
    {
        var report = Check(Good(b => b.Add(new BidRowData(3, 0, BidKind.Manual, Bob, 15m, 15m, K2, false))));

        report.Duplicates.Should().Be(1);
        report.Result.Should().Be("FAIL");
    }

    [Fact]
    public void R4_2_a_gap_in_seq_fails() =>
        Check(Good(events: e => e[1] = new EventData(3, "Bid", "{\"priceC\":1600}"))).SeqGaps.Should().Be(1);

    [Fact]
    public void R4_3_a_final_price_that_replay_does_not_give_fails()
    {
        var report = Check(Good(price: 17m));

        report.ReplayMismatches.Should().Be(1);
        report.Notes.Should().ContainSingle().Which.Should().Contain("replay gives 16");
    }

    [Fact]
    public void R4_3_replay_ignores_voided_bids_and_rows_without_a_max()
    {
        var data = Good(b => b.Add(new BidRowData(3, 0, BidKind.Manual, Bob, 99m, 99m, Guid.NewGuid(), Voided: true)));

        VerifierChecks.ReplayMatches(data, out _).Should().BeTrue();
    }

    [Fact]
    public void R4_4_a_price_that_goes_down_fails() =>
        Check(Good(events: e => e[1] = new EventData(2, "Bid", "{\"priceC\":900}"))).PriceDecreases.Should().Be(1);

    [Fact]
    public void R4_5_more_extension_than_the_cap_fails()
    {
        Check(Good(end: Scheduled.AddSeconds(600))).ExtensionOverCap.Should().Be(0, "exactly the cap is fine");
        Check(Good(end: Scheduled.AddSeconds(700))).ExtensionOverCap.Should().Be(1);
    }

    [Theory]
    [InlineData(AuctionStatus.Sealed)]
    [InlineData(AuctionStatus.Settling)]
    public void R4_6_an_auction_still_sealed_fails(AuctionStatus status) =>
        Check(Good(status: status)).StuckSealed.Should().Be(1);

    [Fact]
    public void R4_7_a_watcher_that_saw_a_different_last_seq_fails()
    {
        var report = Check(Good(), new Dictionary<Guid, long> { [A] = 1 });

        report.SubscriberSeqMismatch.Should().Be(1);
        report.Notes.Should().ContainSingle().Which.Should().Contain("watcher saw seq 1, SQL has 2");
    }

    [Fact]
    public void An_auction_with_no_watcher_is_not_judged_on_R4_7() =>
        Check(Good(), new Dictionary<Guid, long>()).SubscriberSeqMismatch.Should().Be(0);

    [Fact]
    public void Only_the_broken_auction_is_listed()
    {
        var other = Good() with { AuctionId = Guid.NewGuid() };
        var broken = Good(b => b.RemoveAll(r => r.IdempotencyKey == K1));

        var report = VerifierChecks.Check([other, broken], [.. Ledger, new LedgerEntry(other.AuctionId, K1, 1), new LedgerEntry(other.AuctionId, K2, 2)],
            new Dictionary<Guid, long> { [A] = 2, [other.AuctionId] = 2 }, 6);

        report.FailingAuctions.Should().Equal(A.ToString());
    }
}
