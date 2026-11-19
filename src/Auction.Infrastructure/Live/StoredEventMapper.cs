using System.Text.Json;
using System.Text.Json.Serialization;
using Auction.Contracts;
using Auction.Infrastructure.Redis;
using ProtoAuctionEvent = Auction.Contracts.AuctionEvent;

namespace Auction.Infrastructure.Live;

/// <summary>
/// One public event as kept in {pN}:a:{id}:events (written by bid.lua and the lifecycle scripts).
/// It holds only public fields: no max, no reserve amount.
/// </summary>
public sealed record StoredEvent(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("priceC")] long PriceC = 0,
    [property: JsonPropertyName("leader")] string? Leader = null,
    [property: JsonPropertyName("bidCount")] int BidCount = 0,
    [property: JsonPropertyName("endMs")] long EndMs = 0,
    [property: JsonPropertyName("extended")] bool Extended = false,
    [property: JsonPropertyName("reserveMet")] string? ReserveMet = null,
    [property: JsonPropertyName("minNextC")] long? MinNextC = null,
    [property: JsonPropertyName("atMs")] long AtMs = 0,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("outcome")] string? Outcome = null,
    [property: JsonPropertyName("finalC")] long? FinalC = null,
    [property: JsonPropertyName("change")] string? Change = null,
    [property: JsonPropertyName("winner")] string? Winner = null,
    [property: JsonPropertyName("rows")] IReadOnlyList<PublicBidJson>? Rows = null);

/// <summary>Turns stored events into live events for catch-up (spec 04 task 4). Pure.</summary>
public static class StoredEventMapper
{
    /// <summary>Null when the text is not a readable event.</summary>
    public static StoredEvent? Parse(string json)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredEvent>(json);
            return stored is { Seq: > 0 } && stored.Type is "Bid" or "State" ? stored : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IEnumerable<Guid> UsersIn(StoredEvent stored)
    {
        if (Guid.TryParse(stored.Leader, out var leader))
        {
            yield return leader;
        }

        if (Guid.TryParse(stored.Winner, out var winner))
        {
            yield return winner;
        }

        foreach (var row in stored.Rows ?? [])
        {
            yield return row.User;
        }
    }

    public static ProtoAuctionEvent Map(Guid auctionId, StoredEvent stored, string currency, IReadOnlyDictionary<Guid, string> handles)
    {
        var live = new ProtoAuctionEvent { Seq = stored.Seq, ServerTsMs = stored.AtMs, AuctionId = auctionId.ToString() };

        if (stored.Type == "Bid")
        {
            var bid = new BidAccepted
            {
                Price = Money(stored.PriceC, currency),
                Leader = Guid.TryParse(stored.Leader, out var leader) ? User(leader, handles) : null,
                BidCount = stored.BidCount,
                ReserveMet = stored.ReserveMet switch { "1" => true, "0" => false, _ => null },
                EndTimeMs = stored.EndMs,
                Extended = stored.Extended,
                MinNextBid = stored.MinNextC == null ? null : Money(stored.MinNextC.Value, currency),
            };

            foreach (var row in stored.Rows ?? [])
            {
                bid.Rows.Add(new PublicBidRow
                {
                    Seq = row.Seq,
                    Bidder = User(row.User, handles),
                    Amount = Money(row.AmountC, currency),
                    AtMs = row.AtMs,
                    Auto = row.Kind is "ProxyAuto" or "AutoOutbid",
                });
            }

            live.BidAccepted = bid;
            return live;
        }

        live.StateChanged = new StateChanged
        {
            Status = stored.Status ?? "",
            Outcome = stored.Outcome ?? "",
            FinalPrice = stored.FinalC is > 0 ? Money(stored.FinalC.Value, currency) : null,
            EndTimeMs = stored.EndMs,
            Change = stored.Change ?? "",
            Winner = Guid.TryParse(stored.Winner, out var winner) ? User(winner, handles) : null,
        };
        return live;
    }

    private static PublicUser User(Guid id, IReadOnlyDictionary<Guid, string> handles) =>
        new() { UserId = id.ToString(), Handle = handles.GetValueOrDefault(id, "***") };

    private static Contracts.Money Money(long cents, string currency) => Domain.Money.FromCents(cents, currency).ToProto();
}
