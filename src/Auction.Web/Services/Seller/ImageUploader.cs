using Auction.Contracts;
using Auction.Domain.Catalog;
using Google.Protobuf;
using Microsoft.AspNetCore.Components.Forms;

namespace Auction.Web.Services.Seller;

/// <summary>Sends one photo to UploadImage in 64 KB chunks (design 6, step 2), reporting progress 0..1.</summary>
public sealed class ImageUploader(SellerService.SellerServiceClient seller)
{
    public const int ChunkSize = 64 * 1024;

    public static readonly IReadOnlyList<string> Allowed = ["image/jpeg", "image/png", "image/webp"];

    /// <summary>Null when the file is fine to send, else the reason.</summary>
    public static string? Check(IBrowserFile file) =>
        !Allowed.Contains(file.ContentType) ? "Only JPEG, PNG or WebP images are allowed."
        : file.Size is <= 0 or > ImageSniffer.MaxBytes ? "Images must be 5 MB or smaller."
        : null;

    public async Task<Image> UploadAsync(string itemId, IBrowserFile file, Action<double> progress, CancellationToken ct = default)
    {
        using var call = seller.UploadImage(cancellationToken: ct);
        await call.RequestStream.WriteAsync(new UploadImageChunk
        {
            Header = new UploadImageHeader { ItemId = itemId, ContentType = file.ContentType, Bytes = file.Size },
        }, ct);

        await using var stream = file.OpenReadStream(ImageSniffer.MaxBytes, ct);
        var buffer = new byte[ChunkSize];
        long sent = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await call.RequestStream.WriteAsync(new UploadImageChunk { Data = ByteString.CopyFrom(buffer, 0, read) }, ct);
            sent += read;
            progress(sent / (double)file.Size);
        }

        await call.RequestStream.CompleteAsync();
        return await call.ResponseAsync;
    }
}
