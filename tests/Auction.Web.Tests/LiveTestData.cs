using System.Threading.Channels;
using Auction.Contracts;
using Auction.Web.Services.Live;

namespace Auction.Web.Tests;

/// <summary>Builders for live events, all money in USD.</summary>
public static class LiveTestData
{
    public static readonly Guid AuctionId = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    public static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    public static Money Usd(decimal amount) => new Domain.Money(amount, "USD").ToProto();

    public static PublicBidRow Row(long seq, Guid user, string handle, decimal amount, bool auto = false) =>
        new() { Seq = seq, Bidder = new PublicUser { UserId = user.ToString(), Handle = handle }, Amount = Usd(amount), AtMs = seq, Auto = auto };

    public static AuctionEvent Snapshot(long seq, decimal price = 10m, int bidCount = 1, params PublicBidRow[] rows)
    {
        var snapshot = new AuctionSnapshot
        {
            AuctionId = AuctionId.ToString(), Seq = seq, Status = "Live", Currency = "USD", CurrentPrice = Usd(price),
            StartingPrice = Usd(10m), MinNextBid = Usd(price + 1), BidCount = bidCount,
            Leader = new PublicUser { UserId = Alice.ToString(), Handle = "a***2x" }, EndTimeMs = 5000, ServerTsMs = 100,
        };
        snapshot.RecentBids.AddRange(rows);
        return new AuctionEvent { Seq = seq, ServerTsMs = 100, AuctionId = AuctionId.ToString(), Snapshot = snapshot };
    }

    public static AuctionEvent Bid(long seq, decimal price, Guid leader, string leaderHandle, params PublicBidRow[] rows)
    {
        var bid = new BidAccepted
        {
            Price = Usd(price), MinNextBid = Usd(price + 1), BidCount = (int)seq,
            Leader = new PublicUser { UserId = leader.ToString(), Handle = leaderHandle }, EndTimeMs = 6000,
        };
        bid.Rows.AddRange(rows);
        return new AuctionEvent { Seq = seq, ServerTsMs = 200 + seq, AuctionId = AuctionId.ToString(), BidAccepted = bid };
    }

    public static AuctionEvent State(long seq, string status) =>
        new() { Seq = seq, ServerTsMs = 300, AuctionId = AuctionId.ToString(), StateChanged = new StateChanged { Status = status } };

    public static AuctionEvent Heartbeat(long seq) =>
        new() { Seq = seq, ServerTsMs = 900, AuctionId = AuctionId.ToString(), Heartbeat = new Heartbeat() };
}

/// <summary>
/// Stands in for the Api stream. Each SubscribeAsync call gets its own script: push events or an
/// error into the newest call and the feed reads them.
/// </summary>
public sealed class FakeLiveSource : ILiveFeedSource
{
    public sealed record Call(Guid AuctionId, long LastSeenSeq, Channel<object> Script, CancellationToken Token);

    private readonly List<Call> _calls = [];
    private readonly object _gate = new();

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    public Call Latest => Calls[^1];

    public async IAsyncEnumerable<AuctionEvent> SubscribeAsync(Guid auctionId, long lastSeenSeq,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var call = new Call(auctionId, lastSeenSeq, Channel.CreateUnbounded<object>(), ct);
        lock (_gate)
        {
            _calls.Add(call);
        }

        await foreach (var item in call.Script.Reader.ReadAllAsync(ct))
        {
            if (item is Exception error)
            {
                throw error;
            }

            yield return (AuctionEvent)item;
        }
    }

    public void Push(AuctionEvent e) => Latest.Script.Writer.TryWrite(e);

    public void Break() => Latest.Script.Writer.TryWrite(new IOException("stream broke"));
}

public static class Wait
{
    /// <summary>Waits (real time, at most 3 s) until the check is true; the feed runs on its own thread.</summary>
    public static async Task UntilAsync(Func<bool> check, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!check())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(10);
        }
    }
}
