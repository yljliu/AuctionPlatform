using Auction.Api.Security;
using Auction.Contracts;
using Auction.Domain.Bidding;
using Auction.Infrastructure.Bidding;
using Grpc.Core;

namespace Auction.Api.Services;

/// <summary>Bidding calls. GetBidHistory, GetMyBidStatus and ListMyBids come in spec 03 task 10.</summary>
public sealed class BiddingGrpcService(BidService bids, BidReadService reads, Infrastructure.Account.SettlementQueries settlements) : BiddingService.BiddingServiceBase
{
    /// <summary>Public; newest first; names masked except your own (admins see all).</summary>
    public override async Task<GetBidHistoryResponse> GetBidHistory(GetBidHistoryRequest request, ServerCallContext context)
    {
        var history = await reads.GetHistoryAsync(
            CatalogMapper.ParseId(request.AuctionId, "auction_id"),
            context.GetOptionalUserId(),
            context.IsAdmin(),
            request.IncludeVoided,
            Domain.Catalog.Paging.From(request.Page, request.PageSize),
            context.CancellationToken);

        var response = new GetBidHistoryResponse { BidCount = history.BidCount };
        foreach (var row in history.Rows)
        {
            response.Bids.Add(new PublicBidRow
            {
                Seq = row.Seq,
                Bidder = new PublicUser { UserId = row.BidderId.ToString(), Handle = row.Handle },
                Amount = Domain.Money.FromCents(Domain.Bidding.IncrementTable.ToCents(row.Amount), history.Currency).ToProto(),
                AtMs = row.AtMs,
                Auto = row.Auto,
                Voided = row.Voided,
            });
        }

        return response;
    }

    [SignedIn]
    public override async Task<MyBidStatus> GetMyBidStatus(GetMyBidStatusRequest request, ServerCallContext context)
    {
        var status = await reads.GetMyStatusAsync(CatalogMapper.ParseId(request.AuctionId, "auction_id"), context.GetUserId(), context.CancellationToken);

        return new MyBidStatus
        {
            MyMax = status.MyMax == null ? null : new Domain.Money(status.MyMax.Value, status.Currency).ToProto(),
            IsLeader = status.IsLeader,
            MinNextBid = new Domain.Money(status.MinNext, status.Currency).ToProto(),
        };
    }

    [SignedIn]
    public override async Task<ListMyBidsResponse> ListMyBids(ListMyBidsRequest request, ServerCallContext context)
    {
        var filter = request.Filter switch
        {
            ListMyBidsRequest.Types.Filter.Won => MyBidsFilter.Won,
            ListMyBidsRequest.Types.Filter.Lost => MyBidsFilter.Lost,
            ListMyBidsRequest.Types.Filter.All => MyBidsFilter.All,
            _ => MyBidsFilter.Active,
        };

        var (rows, total) = await reads.ListMineAsync(context.GetUserId(), filter,
            Domain.Catalog.Paging.From(request.Page, request.PageSize), context.CancellationToken);

        var response = new ListMyBidsResponse { Total = total };
        foreach (var row in rows)
        {
            var currency = row.Card.Currency;
            response.Rows.Add(new MyBidRow
            {
                Auction = AuctionMapper.ToCard(row.Card),
                MyMax = row.MyMax == null ? null : new Domain.Money(row.MyMax.Value, currency).ToProto(),
                MyLastAmount = row.MyLastAmount == null ? null : new Domain.Money(row.MyLastAmount.Value, currency).ToProto(),
                IsLeader = row.IsLeader,
                Outcome = row.Outcome,
            });
        }

        return response;
    }

    /// <summary>Auctions you won, with what you owe (spec 06, R4.3).</summary>
    [SignedIn]
    public override async Task<ListSettlementsResponse> ListMyPurchases(ListMyPurchasesRequest request, ServerCallContext context)
    {
        var page = await settlements.ForBuyerAsync(context.GetUserId(), Domain.Catalog.Paging.From(request.Page, request.PageSize), context.CancellationToken);
        var response = new ListSettlementsResponse { Total = page.Total };
        response.Rows.AddRange(page.Rows.Select(r => AuctionMapper.ToSettlementRow(r, showBuyer: false)));
        return response;
    }

    [SignedIn]
    public override async Task<PlaceBidResponse> PlaceBid(PlaceBidRequest request, ServerCallContext context)
    {
        var max = request.MaxAmount ?? new Money();
        var outcome = await bids.PlaceAsync(new PlaceBidCommand(
            context.GetUserId(),
            request.AuctionId,
            max.Units,
            max.Nanos,
            max.Currency,
            request.IdempotencyKey,
            context.GetCorrelationId()), context.CancellationToken);

        return ToResponse(outcome, max.Currency.Trim().ToUpperInvariant());
    }

    public static PlaceBidResponse ToResponse(BidOutcome outcome, string currency) => new()
    {
        Result = outcome.Result switch
        {
            BidResult.Leading => PlaceBidResponse.Types.Result.Leading,
            BidResult.OutbidByProxy => PlaceBidResponse.Types.Result.OutbidByProxy,
            BidResult.MaxRaised => PlaceBidResponse.Types.Result.MaxRaised,
            _ => PlaceBidResponse.Types.Result.Unspecified,
        },
        CurrentPrice = Domain.Money.FromCents(outcome.PriceC, currency).ToProto(),
        MinNextBid = Domain.Money.FromCents(outcome.MinNextC, currency).ToProto(),
        Seq = outcome.Seq,
        EndTimeMs = outcome.EndMs,
        Extended = outcome.Extended,
        IsLeader = outcome.IsLeader,
        Replayed = outcome.Replayed,
    };
}
