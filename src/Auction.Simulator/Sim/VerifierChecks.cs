using System.Text.Json;
using System.Text.Json.Serialization;
using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Domain.Errors;

namespace Auction.Simulator.Sim;

/// <summary>One bidding.Bids row, as the verifier reads it.</summary>
public sealed record BidRowData(long ESeq, byte Idx, BidKind Kind, Guid UserId, decimal Amount, decimal? MaxAmount, Guid? IdempotencyKey, bool Voided);

/// <summary>One persisted public event (bidding.AuctionEvents with a seq).</summary>
public sealed record EventData(long Seq, string EventType, string Payload);

/// <summary>What SQL says about one auction at the end.</summary>
public sealed record AuctionData(
    Guid AuctionId,
    AuctionStatus Status,
    decimal? CurrentPrice,
    Guid? CurrentLeaderId,
    DateTime EndTime,
    DateTime ScheduledEndTime,
    int MaxTotalExtensionSec,
    decimal StartingPrice,
    IncrementTable Increments,
    IReadOnlyList<BidRowData> Bids,
    IReadOnlyList<EventData> Events);

/// <summary>One accepted bid the bots saw (the ledger, design 1).</summary>
public sealed record LedgerEntry(Guid AuctionId, Guid IdempotencyKey, long Seq);

/// <summary>The verifier's result (design 2). Counts are over all target auctions.</summary>
public sealed class VerifyReport
{
    [JsonPropertyName("result")] public string Result { get; set; } = "PASS";
    [JsonPropertyName("auctions")] public int Auctions { get; set; }
    [JsonPropertyName("bidsSent")] public long BidsSent { get; set; }
    [JsonPropertyName("accepted")] public long Accepted { get; set; }
    [JsonPropertyName("missingInSql")] public int MissingInSql { get; set; }
    [JsonPropertyName("duplicates")] public int Duplicates { get; set; }
    [JsonPropertyName("seqGaps")] public int SeqGaps { get; set; }
    [JsonPropertyName("replayMismatches")] public int ReplayMismatches { get; set; }
    [JsonPropertyName("priceDecreases")] public int PriceDecreases { get; set; }
    [JsonPropertyName("extensionOverCap")] public int ExtensionOverCap { get; set; }
    [JsonPropertyName("stuckSealed")] public int StuckSealed { get; set; }
    [JsonPropertyName("subscriberSeqMismatch")] public int SubscriberSeqMismatch { get; set; }
    [JsonPropertyName("failingAuctions")] public List<string> FailingAuctions { get; set; } = [];
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = [];

    public bool Passed => Result == "PASS";

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>The invariant checks R4.1 to R4.7 (spec 08 design 2). Pure, so each has a unit test.</summary>
public static class VerifierChecks
{
    /// <param name="subscriberSeq">The last seq each simulator watcher saw, per auction (missing = no watcher).</param>
    public static VerifyReport Check(IReadOnlyList<AuctionData> auctions, IReadOnlyList<LedgerEntry> ledger,
        IReadOnlyDictionary<Guid, long> subscriberSeq, long bidsSent)
    {
        var report = new VerifyReport { Auctions = auctions.Count, BidsSent = bidsSent, Accepted = ledger.Count };
        foreach (var a in auctions)
        {
            var failed = false;
            var mine = ledger.Where(l => l.AuctionId == a.AuctionId).ToList();

            // R4.1: every accepted bid is in Bids exactly once (by idempotency key).
            var keyCounts = a.Bids.Where(b => b.IdempotencyKey != null).GroupBy(b => b.IdempotencyKey!.Value).ToDictionary(g => g.Key, g => g.Count());
            foreach (var entry in mine)
            {
                var n = keyCounts.GetValueOrDefault(entry.IdempotencyKey);
                if (n == 0)
                {
                    report.MissingInSql++;
                    failed = true;
                }
                else if (n > 1)
                {
                    report.Duplicates++;
                    failed = true;
                }
            }

            // R4.2: public seq has no gaps (1, 2, 3, ...).
            var seqs = a.Events.Select(e => e.Seq).Distinct().OrderBy(s => s).ToList();
            for (var i = 0; i < seqs.Count; i++)
            {
                if (seqs[i] != i + 1)
                {
                    report.SeqGaps++;
                    failed = true;
                    break;
                }
            }

            // R4.3: replaying the accepted bids in eseq order gives the price and leader SQL has.
            if (!ReplayMatches(a, out var note))
            {
                report.ReplayMismatches++;
                report.Notes.Add($"{a.AuctionId}: {note}");
                failed = true;
            }

            // R4.4: the price never goes down.
            long last = 0;
            foreach (var e in a.Events.Where(e => e.EventType == "Bid").OrderBy(e => e.Seq))
            {
                var price = PriceOf(e.Payload);
                if (price < last)
                {
                    report.PriceDecreases++;
                    failed = true;
                    break;
                }

                last = Math.Max(last, price);
            }

            // R4.5: extension used is within the cap.
            if ((a.EndTime - a.ScheduledEndTime).TotalSeconds > a.MaxTotalExtensionSec + 1)
            {
                report.ExtensionOverCap++;
                failed = true;
            }

            // R4.6: nothing stays Sealed.
            if (a.Status is AuctionStatus.Sealed or AuctionStatus.Settling)
            {
                report.StuckSealed++;
                failed = true;
            }

            // R4.7: the watcher saw the same last seq as SQL.
            if (subscriberSeq.TryGetValue(a.AuctionId, out var seen) && seqs.Count > 0 && seen != seqs[^1])
            {
                report.SubscriberSeqMismatch++;
                report.Notes.Add($"{a.AuctionId}: watcher saw seq {seen}, SQL has {seqs[^1]}");
                failed = true;
            }

            if (failed)
            {
                report.FailingAuctions.Add(a.AuctionId.ToString());
            }
        }

        report.Result = report.FailingAuctions.Count == 0 ? "PASS" : "FAIL";
        return report;
    }

    /// <summary>Feeds each stream entry's bid (the row with the max) through ProxyResolver and compares the end state.</summary>
    public static bool ReplayMatches(AuctionData a, out string note)
    {
        var state = new BidState(0, null, 0, 0, IncrementTable.ToCents(a.StartingPrice));
        foreach (var entry in a.Bids.Where(b => !b.Voided).GroupBy(b => b.ESeq).OrderBy(g => g.Key))
        {
            var bid = entry.FirstOrDefault(b => b.MaxAmount != null);
            if (bid == null)
            {
                continue;
            }

            try
            {
                state = ProxyResolver.Resolve(state, bid.UserId, IncrementTable.ToCents(bid.MaxAmount!.Value), a.Increments).Next;
            }
            catch (DomainException)
            {
                note = $"bid at eseq {entry.Key} would be refused on replay";
                return false;
            }
        }

        var sqlPriceC = a.CurrentPrice == null ? 0 : IncrementTable.ToCents(a.CurrentPrice.Value);
        var replayPriceC = state.BidCount == 0 ? 0 : state.PriceC;
        if (state.BidCount > 0 && (replayPriceC != sqlPriceC || state.Leader != a.CurrentLeaderId))
        {
            note = $"replay gives {replayPriceC / 100m} led by {state.Leader}, SQL has {sqlPriceC / 100m} led by {a.CurrentLeaderId}";
            return false;
        }

        note = "";
        return true;
    }

    private static long PriceOf(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("priceC", out var p) && p.TryGetInt64(out var c) ? c : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
