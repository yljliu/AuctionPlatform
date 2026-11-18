using Auction.Api.Security;
using Auction.Contracts;
using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Identity;
using Grpc.Core;

namespace Auction.Api.Services;

/// <summary>Public catalog. Works signed out. Browse, search, detail and images come in later tasks.</summary>
public sealed class CatalogGrpcService(
    CategoryRepository categories,
    ImageManager images,
    AuctionBrowser browser,
    AuctionDetailReader details,
    TimeProvider clock)
    : CatalogService.CatalogServiceBase
{
    private const int ChunkSize = 64 * 1024;

    public override async Task<ListAuctionsResponse> ListAuctions(ListAuctionsRequest request, ServerCallContext context)
    {
        var search = await ToSearchAsync(request.Filter, request.Sort, words: null, context.CancellationToken);
        return await RunAsync(search, Paging.From(request.Page, request.PageSize), context);
    }

    public override async Task<ListAuctionsResponse> SearchAuctions(SearchAuctionsRequest request, ServerCallContext context)
    {
        var words = SearchText.Words(request.Query);
        var search = await ToSearchAsync(request.Filter, request.Sort, words, context.CancellationToken);
        return await RunAsync(search, Paging.From(request.Page, request.PageSize), context);
    }

    /// <summary>Public auction page; the owner and admins get the seller view with the reserve.</summary>
    public override async Task<GetAuctionResponse> GetAuction(GetAuctionRequest request, ServerCallContext context)
    {
        var data = await details.GetAsync(
            CatalogMapper.ParseId(request.AuctionId, "auction_id"),
            context.GetOptionalUserId(),
            context.IsAdmin(),
            context.CancellationToken);

        return data.IsSellerView
            ? new GetAuctionResponse { SellerView = AuctionMapper.ToSellerAuction(data) }
            : new GetAuctionResponse { Detail = AuctionMapper.ToDetail(data) };
    }

    private async Task<ListAuctionsResponse> RunAsync(AuctionSearch search, Paging paging, ServerCallContext context)
    {
        var result = await browser.BrowseAsync(search, paging, context.GetOptionalUserId(), context.CancellationToken);

        var response = new ListAuctionsResponse
        {
            Total = result.Total,
            Page = paging.Page,
            PageSize = paging.Size,
            PricesDelayed = result.PricesDelayed,
        };
        foreach (var row in result.Rows)
        {
            response.Auctions.Add(AuctionMapper.ToCard(row, result.Watching.Contains(row.AuctionId), result.PricesDelayed));
        }

        return response;
    }

    /// <summary>Request filter to SQL filter. Bad values give VALIDATION_FAILED with the field name.</summary>
    private async Task<AuctionSearch> ToSearchAsync(AuctionFilter? filter, AuctionSort sort, IReadOnlyList<string>? words, CancellationToken ct)
    {
        filter ??= new AuctionFilter();
        var issues = new List<FieldIssue>();

        var status = AuctionStatus.Live;
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && (!EnumText.TryParse(filter.Status, out status) || !AuctionQueries.BrowsableStatuses.Contains(status)))
        {
            issues.Add(new FieldIssue("filter.status", "one_of", "Use Scheduled, Live, Sealed, Settling or Settled."));
        }

        IReadOnlyList<int>? categoryIds = null;
        if (filter.CategoryId != null)
        {
            var tree = await categories.GetTreeAsync(ct);
            categoryIds = CategoryTree.SelfAndDescendants(tree, filter.CategoryId.Value);   // unknown or hidden: empty, so no results
        }

        var min = filter.MinPrice?.ToDomain().Amount;
        var max = filter.MaxPrice?.ToDomain().Amount;
        if (min < 0)
        {
            issues.Add(new FieldIssue("filter.min_price", "min", "Must be 0 or more."));
        }

        if (min != null && max != null && min > max)
        {
            issues.Add(new FieldIssue("filter.max_price", "range", "Max price must be at least the min price."));
        }

        DateTime? endingBefore = null;
        if (filter.EndingWithinSec != null)
        {
            if (filter.EndingWithinSec.Value <= 0)
            {
                issues.Add(new FieldIssue("filter.ending_within_sec", "min", "Must be more than 0."));
            }
            else
            {
                endingBefore = clock.GetUtcNow().UtcDateTime.AddSeconds(filter.EndingWithinSec.Value);
            }
        }

        Guid? sellerId = null;
        if (!string.IsNullOrWhiteSpace(filter.SellerId))
        {
            if (Guid.TryParse(filter.SellerId, out var parsed))
            {
                sellerId = parsed;
            }
            else
            {
                issues.Add(new FieldIssue("filter.seller_id", "format", "Must be a UUID."));
            }
        }

        var conditions = new List<ItemCondition>();
        foreach (var text in filter.Conditions)
        {
            if (EnumText.TryParse<ItemCondition>(text, out var condition))
            {
                conditions.Add(condition);
            }
            else
            {
                issues.Add(new FieldIssue("filter.conditions", "one_of", $"Unknown condition '{text}'."));
            }
        }

        if (issues.Count > 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Some filters are not valid.", issues);
        }

        return new AuctionSearch(status, categoryIds, min, max, endingBefore, sellerId, conditions, words, ToSortOrder(sort));
    }

    private static AuctionSortOrder ToSortOrder(AuctionSort sort) => sort switch
    {
        AuctionSort.Newest => AuctionSortOrder.Newest,
        AuctionSort.PriceAsc => AuctionSortOrder.PriceAsc,
        AuctionSort.PriceDesc => AuctionSortOrder.PriceDesc,
        AuctionSort.MostBids => AuctionSortOrder.MostBids,
        _ => AuctionSortOrder.EndingSoon,
    };

    public override async Task GetImage(GetImageRequest request, IServerStreamWriter<ImageChunk> responseStream, ServerCallContext context)
    {
        var id = CatalogMapper.ParseId(request.ImageId, "image_id");
        var stored = await images.FindAsync(id, request.Thumb, context.CancellationToken)
                     ?? throw new DomainException(ErrorCodes.NotFound, "Image not found.");

        await using var file = new FileStream(stored.Path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
        var buffer = new byte[ChunkSize];
        var first = true;
        int read;
        while ((read = await file.ReadAsync(buffer, context.CancellationToken)) > 0)
        {
            var chunk = new ImageChunk { Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read) };
            if (first)
            {
                chunk.ContentType = stored.ContentType;
                first = false;
            }

            await responseStream.WriteAsync(chunk, context.CancellationToken);
        }
    }

    public override async Task<GetCategoryTreeResponse> GetCategoryTree(GetCategoryTreeRequest request, ServerCallContext context)
    {
        var tree = await categories.GetTreeAsync(context.CancellationToken);

        var response = new GetCategoryTreeResponse();
        foreach (var node in tree)
        {
            response.Roots.Add(ToProto(node));
        }

        return response;
    }

    public override async Task<ListCategoriesResponse> ListCategories(ListCategoriesRequest request, ServerCallContext context)
    {
        if (request.IncludeInactive && !context.GetHttpContext().User.HasClaim(AuthClaims.Role, nameof(Role.Admin)))
        {
            throw new DomainException(ErrorCodes.RoleRequired, "Only admins can see inactive categories.");
        }

        var rows = await categories.ListAsync(request.ParentCategoryId, request.IncludeInactive, context.CancellationToken);

        var response = new ListCategoriesResponse();
        foreach (var row in rows)
        {
            response.Categories.Add(ToProto(row));
        }

        return response;
    }

    private static Category ToProto(CategoryNode node)
    {
        var category = new Category
        {
            CategoryId = node.Id,
            ParentCategoryId = node.ParentId,
            Name = node.Name,
            Slug = node.Slug,
            Description = node.Description ?? "",
            SortOrder = node.SortOrder,
            IsActive = node.IsActive,
        };
        foreach (var child in node.Children)
        {
            category.Children.Add(ToProto(child));
        }

        return category;
    }

    private static Category ToProto(CategoryRow row) => new()
    {
        CategoryId = row.Id,
        ParentCategoryId = row.ParentId,
        Name = row.Name,
        Slug = row.Slug,
        Description = row.Description ?? "",
        SortOrder = row.SortOrder,
        IsActive = row.IsActive,
    };
}
