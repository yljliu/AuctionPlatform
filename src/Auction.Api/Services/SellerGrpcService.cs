using Auction.Api.Security;
using Auction.Contracts;
using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Infrastructure.Catalog;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Auction.Api.Services;

/// <summary>Seller actions. Images and auctions come in tasks 4 to 6.</summary>
[RequireRole(Role.Seller)]
public sealed class SellerGrpcService(ItemManager items, ImageManager images, AuctionManager auctions, Infrastructure.Account.SettlementQueries settlements) : SellerService.SellerServiceBase
{
    public override async Task<SellerAuction> CreateAuction(CreateAuctionRequest request, ServerCallContext context)
    {
        var view = await auctions.CreateAsync(
            context.GetUserId(),
            CatalogMapper.ParseId(request.ItemId, "item_id"),
            AuctionMapper.ToInput(request.Fields),
            context.CancellationToken);
        return AuctionMapper.ToSellerAuction(view);
    }

    public override async Task<SellerAuction> UpdateAuction(UpdateAuctionRequest request, ServerCallContext context)
    {
        var view = await auctions.UpdateAsync(
            context.GetUserId(),
            CatalogMapper.ParseId(request.AuctionId, "auction_id"),
            request.RowVersion,
            AuctionMapper.ToInput(request.Fields),
            context.CancellationToken);
        return AuctionMapper.ToSellerAuction(view);
    }

    public override async Task<SellerAuction> CancelAuction(CancelAuctionRequest request, ServerCallContext context)
    {
        var view = await auctions.CancelAsync(
            context.GetUserId(),
            CatalogMapper.ParseId(request.AuctionId, "auction_id"),
            request.Reason,
            context.CancellationToken);
        return AuctionMapper.ToSellerAuction(view);
    }

    public override async Task<ListMyAuctionsResponse> ListMyAuctions(ListMyAuctionsRequest request, ServerCallContext context)
    {
        var (views, total) = await auctions.ListMineAsync(context.GetUserId(), request.Status,
            Paging.From(request.Page, request.PageSize), context.CancellationToken);

        var response = new ListMyAuctionsResponse { Total = total };
        foreach (var view in views)
        {
            response.Auctions.Add(AuctionMapper.ToSellerAuction(view));
        }

        return response;
    }

    /// <summary>Your sold auctions: buyer handle, total due and deadline (spec 06, R5.4).</summary>
    public override async Task<ListSettlementsResponse> ListMySales(ListMySalesRequest request, ServerCallContext context)
    {
        var page = await settlements.ForSellerAsync(context.GetUserId(), Paging.From(request.Page, request.PageSize), context.CancellationToken);
        var response = new ListSettlementsResponse { Total = page.Total };
        response.Rows.AddRange(page.Rows.Select(r => AuctionMapper.ToSettlementRow(r, showBuyer: true)));
        return response;
    }

    /// <summary>First message must be the header, then the file in data chunks.</summary>
    public override async Task<Image> UploadImage(IAsyncStreamReader<UploadImageChunk> requestStream, ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken) || requestStream.Current.PartCase != UploadImageChunk.PartOneofCase.Header)
        {
            throw new Domain.Errors.DomainException(Domain.Errors.ErrorCodes.UploadInvalid, "The first message must be the upload header.");
        }

        var first = requestStream.Current.Header;
        var header = new UploadHeader(CatalogMapper.ParseId(first.ItemId, "item_id"), first.ContentType, first.Bytes);

        var row = await images.UploadAsync(context.GetUserId(), header, ReadData(requestStream, context.CancellationToken), context.CancellationToken);
        return CatalogMapper.ToProto(row);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadData(
        IAsyncStreamReader<UploadImageChunk> stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await stream.MoveNext(ct))
        {
            if (stream.Current.PartCase != UploadImageChunk.PartOneofCase.Data)
            {
                throw new Domain.Errors.DomainException(Domain.Errors.ErrorCodes.UploadInvalid, "Only one header is allowed.");
            }

            yield return stream.Current.Data.Memory;
        }
    }

    public override async Task<Item> CreateItem(CreateItemRequest request, ServerCallContext context)
    {
        var created = await items.CreateAsync(context.GetUserId(), CatalogMapper.ToInput(request.Fields), context.CancellationToken);
        return CatalogMapper.ToProto(created);
    }

    public override async Task<Item> UpdateItem(UpdateItemRequest request, ServerCallContext context)
    {
        var updated = await items.UpdateAsync(
            context.GetUserId(),
            CatalogMapper.ParseId(request.ItemId, "item_id"),
            request.RowVersion,
            CatalogMapper.ToInput(request.Fields),
            context.CancellationToken);
        return CatalogMapper.ToProto(updated);
    }

    public override async Task<Empty> DeleteDraftItem(DeleteDraftItemRequest request, ServerCallContext context)
    {
        await items.DeleteDraftAsync(context.GetUserId(), CatalogMapper.ParseId(request.ItemId, "item_id"), context.CancellationToken);
        return new Empty();
    }

    public override async Task<ListMyItemsResponse> ListMyItems(ListMyItemsRequest request, ServerCallContext context)
    {
        var page = await items.ListMineAsync(context.GetUserId(), request.Status, Paging.From(request.Page, request.PageSize), context.CancellationToken);

        var response = new ListMyItemsResponse { Total = page.Total };
        foreach (var item in page.Items)
        {
            response.Items.Add(CatalogMapper.ToProto(item));
        }

        return response;
    }
}
