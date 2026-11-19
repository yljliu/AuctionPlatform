using Auction.Contracts;
using Auction.Infrastructure.Live;
using Grpc.Core;

namespace Auction.Api.Services;

/// <summary>Live updates (spec 04). Public: works signed out.</summary>
public sealed class LiveGrpcService(SnapshotBuilder snapshots, AuctionStreamer streamer) : LiveService.LiveServiceBase
{
    public override Task<AuctionSnapshot> GetAuctionSnapshot(GetAuctionSnapshotRequest request, ServerCallContext context) =>
        snapshots.BuildAsync(CatalogMapper.ParseId(request.AuctionId, "auction_id"), context.CancellationToken);

    /// <summary>Snapshot or missed events first, then live events, heartbeat every 15 s (R1).</summary>
    public override async Task SubscribeAuction(SubscribeAuctionRequest request, IServerStreamWriter<AuctionEvent> responseStream, ServerCallContext context)
    {
        var auctionId = CatalogMapper.ParseId(request.AuctionId, "auction_id");
        try
        {
            await streamer.RunAsync(auctionId, Math.Max(0, request.LastSeenSeq), e => responseStream.WriteAsync(e), context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // the client left
        }
    }
}
