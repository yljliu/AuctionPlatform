using System.Text.Json;
using Auction.Domain.Enums;

namespace Auction.Infrastructure.Bidding.Persistence;

/// <summary>One bidding.Bids row to insert (skipped if (AuctionId, ESeq, Idx) already exists).</summary>
public sealed record BidInsert(
    Guid AuctionId, Guid UserId, long ESeq, byte Idx, long? Seq, BidKind Kind, decimal Amount,
    decimal? MaxAmount, Guid? IdempotencyKey, DateTime AcceptedAt);

/// <summary>One bidding.AuctionEvents row to insert (skipped if (AuctionId, Seq, EventType) already exists).</summary>
public sealed record EventInsert(Guid AuctionId, long Seq, long ESeq, short EventType, string Payload, DateTime OccurredAt);

/// <summary>
/// The catalog.Auctions change for one auction in a batch. Applied only if the row's LastESeq is
/// older, so a replayed batch never moves the auction back.
/// </summary>
public sealed record AuctionUpdate(
    Guid AuctionId,
    long LastESeq,
    long LastSeq,
    long MaxESeq,
    bool HasBid,
    decimal? CurrentPrice,
    Guid? CurrentLeaderId,
    int BidCount,
    DateTime? EndTime);

public sealed record PersistPlan(
    IReadOnlyList<BidInsert> Bids,
    IReadOnlyList<EventInsert> Events,
    IReadOnlyList<AuctionUpdate> Auctions);

/// <summary>Turns stream entries (in stream order) into the SQL rows to write (spec 03 design 6).</summary>
public static class PersistPlanner
{
    public const short BidEvent = 1;
    public const short StateEvent = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PersistPlan Build(IReadOnlyList<PersistEntry> entries)
    {
        var bids = new List<BidInsert>();
        var events = new List<EventInsert>();
        var updates = new List<AuctionUpdate>();

        // Group by auction but keep each auction's entries in stream order.
        foreach (var group in entries.GroupBy(e => e.AuctionId))
        {
            var ordered = group.OrderBy(e => e.ESeq).ToList();

            foreach (var entry in ordered)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(entry.AtMs).UtcDateTime;

                if (entry.Type == PersistEntryType.Bid)
                {
                    AddBidRows(entry, at, bids);
                    if (entry.Seq != null)
                    {
                        events.Add(new EventInsert(entry.AuctionId, entry.Seq.Value, entry.ESeq, BidEvent, BidPayload(entry), at));
                    }
                }
                else if (entry.Seq != null)
                {
                    events.Add(new EventInsert(entry.AuctionId, entry.Seq.Value, entry.ESeq, StateEvent, StatePayload(entry), at));
                }
            }

            updates.Add(BuildUpdate(group.Key, ordered));
        }

        return new PersistPlan(bids, events, updates);
    }

    /// <summary>
    /// Each row gets its position (Idx). Only the bidder's own row keeps the max and the
    /// idempotency key: the other rows are the system bidding for someone else.
    /// </summary>
    private static void AddBidRows(PersistEntry entry, DateTime at, List<BidInsert> bids)
    {
        var ownRowTaken = false;
        for (var i = 0; i < entry.Rows!.Count; i++)
        {
            var row = entry.Rows[i];
            var isOwn = !ownRowTaken && row.User == entry.User;
            ownRowTaken |= isOwn;

            bids.Add(new BidInsert(
                entry.AuctionId, row.User, entry.ESeq, (byte)i, entry.Seq, row.Kind, row.AmountC / 100m,
                isOwn ? entry.MaxC / 100m : null,
                isOwn ? entry.IdempotencyKey : null,
                at));
        }
    }

    private static AuctionUpdate BuildUpdate(Guid auctionId, List<PersistEntry> ordered)
    {
        var lastBid = ordered.LastOrDefault(e => e.Type == PersistEntryType.Bid);
        var lastSeq = ordered.Where(e => e.Seq != null).Select(e => e.Seq!.Value).DefaultIfEmpty(0).Max();
        var maxEseq = ordered[^1].ESeq;

        return new AuctionUpdate(
            auctionId,
            LastESeq: maxEseq,
            LastSeq: lastSeq,
            MaxESeq: maxEseq,
            HasBid: lastBid != null,
            CurrentPrice: lastBid == null ? null : lastBid.PriceC / 100m,
            CurrentLeaderId: lastBid?.Leader,
            BidCount: lastBid?.BidCount ?? 0,
            EndTime: lastBid == null ? null : DateTimeOffset.FromUnixTimeMilliseconds(lastBid.EndMs).UtcDateTime);
    }

    // Public event payloads: no max values (R4.6).
    private static string BidPayload(PersistEntry e) => JsonSerializer.Serialize(new
    {
        type = "Bid",
        seq = e.Seq,
        eseq = e.ESeq,
        priceC = e.PriceC,
        leader = e.Leader,
        bidCount = e.BidCount,
        endMs = e.EndMs,
        extended = e.Extended,
    }, Json);

    private static string StatePayload(PersistEntry e) => JsonSerializer.Serialize(new
    {
        type = "State",
        seq = e.Seq,
        eseq = e.ESeq,
        status = e.Status,
        change = e.Change,
        outcome = e.Outcome,
        finalC = e.FinalC,
        winner = e.Winner,
        atMs = e.AtMs,
    }, Json);
}
