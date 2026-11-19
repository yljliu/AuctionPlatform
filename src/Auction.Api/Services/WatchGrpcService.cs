using Auction.Api.Security;
using Auction.Contracts;
using Auction.Domain.Catalog;
using Auction.Infrastructure.Watch;
using Grpc.Core;

namespace Auction.Api.Services;

/// <summary>Watchlist calls (spec 07 task 1). Signed in only.</summary>
[SignedIn]
public sealed class WatchGrpcService(WatchManager watches) : WatchService.WatchServiceBase
{
    public override async Task<WatchAuctionResponse> WatchAuction(WatchAuctionRequest request, ServerCallContext context)
    {
        var result = await watches.WatchAsync(context.GetUserId(), CatalogMapper.ParseId(request.AuctionId, "auction_id"), context.CancellationToken);
        return new WatchAuctionResponse { Watching = result.Watching, Watchers = result.Watchers };
    }

    public override async Task<WatchAuctionResponse> UnwatchAuction(WatchAuctionRequest request, ServerCallContext context)
    {
        var result = await watches.UnwatchAsync(context.GetUserId(), CatalogMapper.ParseId(request.AuctionId, "auction_id"), context.CancellationToken);
        return new WatchAuctionResponse { Watching = result.Watching, Watchers = result.Watchers };
    }

    public override async Task<ListAuctionsResponse> ListWatched(ListWatchedRequest request, ServerCallContext context)
    {
        var paging = Paging.From(request.Page, request.PageSize);
        var result = await watches.ListWatchedAsync(context.GetUserId(), paging, context.CancellationToken);
        var response = new ListAuctionsResponse { Total = result.Total, Page = paging.Page, PageSize = paging.Size, PricesDelayed = result.PricesDelayed };
        response.Auctions.AddRange(result.Rows.Select(r => AuctionMapper.ToCard(r, watching: true, result.PricesDelayed)));
        return response;
    }
}
