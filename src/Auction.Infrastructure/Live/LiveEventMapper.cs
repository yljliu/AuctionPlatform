using Auction.Contracts;
using Auction.Infrastructure.Bidding.Persistence;
using ProtoAuctionEvent = Auction.Contracts.AuctionEvent;

namespace Auction.Infrastructure.Live;

/// <summary>
/// Turns one Redis stream entry into the public live event (spec 04 design 2.1, R2).
/// Only public fields are read: maxC and leaderMaxC in the entry are never copied (R1.5).
/// Returns null for entries that are not public events (MaxRaised has no seq).
/// </summary>
public static class LiveEventMapper
{
    /// <summary>User ids whose handles the mapping needs (leader, row bidders, winner).</summary>
    public static IEnumerable<Guid> UsersIn(PersistEntry entry)
    {
        if (entry.Leader != null)
        {
            yield return entry.Leader.Value;
        }

        if (entry.Winner != null)
        {
            yield return entry.Winner.Value;
        }

        foreach (var row in entry.Rows ?? [])
        {
            yield return row.User;
        }
    }

    /// <param name="handles">Masked handle per user id (see UserHandleCache).</param>
    public static ProtoAuctionEvent? Map(PersistEntry entry, IReadOnlyDictionary<string, string> fields, IReadOnlyDictionary<Guid, string> handles)
    {
        if (entry.Seq == null)
        {
            return null;   // MaxRaised and anything else without a public seq
        }

        var currency = fields.TryGetValue("cur", out var cur) && cur.Length == 3 ? cur : "USD";
        var live = new ProtoAuctionEvent
        {
            Seq = entry.Seq.Value,
            ServerTsMs = entry.AtMs,
            AuctionId = entry.AuctionId.ToString(),
        };

        if (entry.Type == PersistEntryType.Bid)
        {
            var bid = new BidAccepted
            {
                Price = Money(entry.PriceC, currency),
                Leader = entry.Leader == null ? null : User(entry.Leader.Value, handles),
                BidCount = entry.BidCount,
                ReserveMet = ReserveMet(fields),
                EndTimeMs = entry.EndMs,
                Extended = entry.Extended,
                MinNextBid = fields.TryGetValue("minNextC", out var minText) && long.TryParse(minText, out var minC) ? Money(minC, currency) : null,
            };

            foreach (var row in entry.Rows ?? [])
            {
                if (row.Kind == Domain.Enums.BidKind.MaxRaise)
                {
                    continue;   // private
                }

                bid.Rows.Add(new PublicBidRow
                {
                    Seq = entry.Seq.Value,
                    Bidder = User(row.User, handles),
                    Amount = Money(row.AmountC, currency),
                    AtMs = entry.AtMs,
                    Auto = row.Kind is Domain.Enums.BidKind.ProxyAuto or Domain.Enums.BidKind.AutoOutbid,
                });
            }

            live.BidAccepted = bid;
            return live;
        }

        var state = new StateChanged { Status = entry.Status ?? "", Change = entry.Change ?? "" };
        if (entry.Winner != null)
        {
            state.Winner = User(entry.Winner.Value, handles);
        }

        if (fields.TryGetValue("outcome", out var outcome))
        {
            state.Outcome = outcome;
        }

        if (fields.TryGetValue("finalC", out var finalText) && long.TryParse(finalText, out var finalC) && finalC > 0)
        {
            state.FinalPrice = Money(finalC, currency);
        }

        if (fields.TryGetValue("endMs", out var endText) && long.TryParse(endText, out var endMs))
        {
            state.EndTimeMs = endMs;
        }

        live.StateChanged = state;
        return live;
    }

    private static bool? ReserveMet(IReadOnlyDictionary<string, string> fields) =>
        fields.TryGetValue("reserveMet", out var value) ? value switch { "1" => true, "0" => false, _ => null } : null;

    private static PublicUser User(Guid id, IReadOnlyDictionary<Guid, string> handles) =>
        new() { UserId = id.ToString(), Handle = handles.GetValueOrDefault(id, "***") };

    private static Contracts.Money Money(long cents, string currency) => Domain.Money.FromCents(cents, currency).ToProto();
}
