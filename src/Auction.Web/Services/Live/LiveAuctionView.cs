using System.Collections.Immutable;
using Auction.Contracts;

namespace Auction.Web.Services.Live;

/// <summary>One public bid row as the Web shows it. The handle is always masked here.</summary>
public sealed record LiveBidRow(long Seq, Guid BidderId, string Handle, decimal Amount, long AtMs, bool Auto);

/// <summary>
/// The live picture of one auction, kept by a feed and read by every component that shows it.
/// Immutable: Apply returns a new view, so readers on other threads always see a whole one.
/// </summary>
public sealed record LiveAuctionView(
    Guid AuctionId,
    bool HasSnapshot,
    long Seq,
    string Status,
    string Currency,
    decimal Price,
    decimal MinNextBid,
    int BidCount,
    Guid? LeaderId,
    string? LeaderHandle,
    long EndTimeMs,
    bool? ReserveMet,
    bool Extended,
    ImmutableList<LiveBidRow> Rows,
    long ServerTsMs)
{
    public const int MaxRows = 50;

    /// <summary>Settled only: Sold or Unsold, from the Settled event (spec 05). Null when not known.</summary>
    public string? Outcome { get; init; }

    /// <summary>Settled and Sold: the winner's masked handle.</summary>
    public string? WinnerHandle { get; init; }

    /// <summary>The last state change seen (Opened, ClosingSoon, Sealed, ...), for the page.</summary>
    public string? LastChange { get; init; }

    /// <summary>Sold or Unsold: from the event, else by the settle rule (bids and reserve not missed).</summary>
    public string? OutcomeOrGuess => Status != "Settled" ? null
        : !string.IsNullOrEmpty(Outcome) ? Outcome
        : BidCount > 0 && ReserveMet != false ? "Sold" : "Unsold";

    public static LiveAuctionView Empty(Guid auctionId) =>
        new(auctionId, false, 0, "", "USD", 0, 0, 0, null, null, 0, null, false, [], 0);

    /// <summary>The view after one event. Events at or below the current seq change nothing (no repeats).</summary>
    public LiveAuctionView Apply(AuctionEvent e)
    {
        switch (e.BodyCase)
        {
            case AuctionEvent.BodyOneofCase.Snapshot:
                return FromSnapshot(e.Snapshot);

            case AuctionEvent.BodyOneofCase.Heartbeat:
                return this with { ServerTsMs = Math.Max(ServerTsMs, e.ServerTsMs) };

            case AuctionEvent.BodyOneofCase.BidAccepted when e.Seq > Seq:
                var bid = e.BidAccepted;
                var newRows = bid.Rows.Reverse().Select(ToRow);   // event rows are oldest first; the view is newest first
                return this with
                {
                    Seq = e.Seq,
                    Price = Amount(bid.Price, Price),
                    MinNextBid = Amount(bid.MinNextBid, MinNextBid),
                    BidCount = bid.BidCount,
                    LeaderId = ParseId(bid.Leader?.UserId),
                    LeaderHandle = bid.Leader?.Handle,
                    EndTimeMs = bid.EndTimeMs > 0 ? bid.EndTimeMs : EndTimeMs,
                    ReserveMet = bid.ReserveMet ?? ReserveMet,
                    Extended = Extended || bid.Extended,
                    Rows = newRows.Concat(Rows).Take(MaxRows).ToImmutableList(),
                    ServerTsMs = Math.Max(ServerTsMs, e.ServerTsMs),
                };

            case AuctionEvent.BodyOneofCase.StateChanged when e.Seq > Seq:
                var state = e.StateChanged;
                return this with
                {
                    Seq = e.Seq,
                    Status = state.Status,
                    EndTimeMs = state.EndTimeMs > 0 ? state.EndTimeMs : EndTimeMs,
                    Price = state.FinalPrice == null ? Price : state.FinalPrice.ToDomain().Amount,
                    ServerTsMs = Math.Max(ServerTsMs, e.ServerTsMs),
                    Outcome = string.IsNullOrEmpty(state.Outcome) ? Outcome : state.Outcome,
                    WinnerHandle = state.Winner?.Handle ?? WinnerHandle,
                    LastChange = string.IsNullOrEmpty(state.Change) ? LastChange : state.Change,
                };

            default:
                return this;   // an event we already have
        }
    }

    private LiveAuctionView FromSnapshot(AuctionSnapshot s) => new(
        AuctionId,
        HasSnapshot: true,
        Seq: s.Seq,
        Status: s.Status,
        Currency: s.Currency,
        Price: Amount(s.CurrentPrice, 0),
        MinNextBid: Amount(s.MinNextBid, 0),
        BidCount: s.BidCount,
        LeaderId: ParseId(s.Leader?.UserId),
        LeaderHandle: s.Leader?.Handle,
        EndTimeMs: s.EndTimeMs,
        ReserveMet: s.ReserveMet,
        Extended: s.ExtensionUsedSec > 0,
        Rows: s.RecentBids.Take(MaxRows).Select(ToRow).ToImmutableList(),
        ServerTsMs: s.ServerTsMs);

    private static LiveBidRow ToRow(PublicBidRow r) =>
        new(r.Seq, ParseId(r.Bidder?.UserId) ?? Guid.Empty, r.Bidder?.Handle ?? "***", Amount(r.Amount, 0), r.AtMs, r.Auto);

    private static decimal Amount(Contracts.Money? money, decimal fallback) => money == null ? fallback : money.ToDomain().Amount;

    private static Guid? ParseId(string? text) => Guid.TryParse(text, out var id) ? id : null;
}
