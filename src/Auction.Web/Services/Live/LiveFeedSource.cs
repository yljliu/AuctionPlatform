using System.Runtime.CompilerServices;
using Auction.Contracts;
using Grpc.Core;

namespace Auction.Web.Services.Live;

/// <summary>Where a feed gets its events. The real one is the Api's SubscribeAuction stream; tests use a fake.</summary>
public interface ILiveFeedSource
{
    IAsyncEnumerable<AuctionEvent> SubscribeAsync(Guid auctionId, long lastSeenSeq, CancellationToken ct);
}

/// <summary>Calls LiveService.SubscribeAuction. Live updates are public, so no user token is sent.</summary>
public sealed class GrpcLiveFeedSource(LiveService.LiveServiceClient client) : ILiveFeedSource
{
    public async IAsyncEnumerable<AuctionEvent> SubscribeAsync(Guid auctionId, long lastSeenSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        using var call = client.SubscribeAuction(
            new SubscribeAuctionRequest { AuctionId = auctionId.ToString(), LastSeenSeq = lastSeenSeq },
            cancellationToken: ct);

        await foreach (var e in call.ResponseStream.ReadAllAsync(ct))
        {
            yield return e;
        }
    }
}
