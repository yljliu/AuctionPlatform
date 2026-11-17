using Auction.Contracts;
using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;

namespace Auction.Web.Images;

/// <summary>
/// Serves item images at /img/{id} and /img/{id}/thumb (R3.3). Bytes come from the Api's
/// GetImage stream and are kept in memory for 5 minutes.
/// </summary>
public static class ImageEndpoints
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);

    private sealed record CachedImage(byte[] Bytes, string ContentType);

    public static void MapImageEndpoints(this WebApplication app)
    {
        app.MapGet("/img/{id:guid}", (Guid id, CatalogService.CatalogServiceClient catalog, IMemoryCache cache, CancellationToken ct) =>
            ServeAsync(id, thumb: false, catalog, cache, ct));

        app.MapGet("/img/{id:guid}/thumb", (Guid id, CatalogService.CatalogServiceClient catalog, IMemoryCache cache, CancellationToken ct) =>
            ServeAsync(id, thumb: true, catalog, cache, ct));
    }

    private static async Task<IResult> ServeAsync(Guid id, bool thumb, CatalogService.CatalogServiceClient catalog, IMemoryCache cache, CancellationToken ct)
    {
        var key = $"img:{id}:{(thumb ? "t" : "f")}";
        if (!cache.TryGetValue(key, out CachedImage? image) || image == null)
        {
            try
            {
                image = await DownloadAsync(id, thumb, catalog, ct);
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.InvalidArgument)
            {
                return Results.NotFound();
            }

            cache.Set(key, image, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTime, Size = image.Bytes.Length });
        }

        return Results.File(image.Bytes, image.ContentType);
    }

    private static async Task<CachedImage> DownloadAsync(Guid id, bool thumb, CatalogService.CatalogServiceClient catalog, CancellationToken ct)
    {
        using var call = catalog.GetImage(new GetImageRequest { ImageId = id.ToString(), Thumb = thumb }, cancellationToken: ct);
        using var buffer = new MemoryStream();
        var contentType = "application/octet-stream";

        await foreach (var chunk in call.ResponseStream.ReadAllAsync(ct))
        {
            if (!string.IsNullOrEmpty(chunk.ContentType))
            {
                contentType = chunk.ContentType;
            }

            chunk.Data.WriteTo(buffer);
        }

        return new CachedImage(buffer.ToArray(), contentType);
    }
}
