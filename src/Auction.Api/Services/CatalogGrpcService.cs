using Auction.Contracts;
using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Identity;
using Grpc.Core;

namespace Auction.Api.Services;

/// <summary>Public catalog. Works signed out. Browse, search, detail and images come in later tasks.</summary>
public sealed class CatalogGrpcService(CategoryRepository categories, ImageManager images) : CatalogService.CatalogServiceBase
{
    private const int ChunkSize = 64 * 1024;

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
