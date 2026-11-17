using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;

namespace Auction.Infrastructure.Catalog;

/// <summary>Where image files live. In Docker this is the "images" volume at /data/images.</summary>
public sealed class ImageStoreOptions
{
    public string RootPath { get; set; } = "/data/images";
}

public sealed record UploadHeader(Guid ItemId, string? ContentType, long Bytes);

public sealed record StoredImage(string Path, string ContentType);

/// <summary>
/// Image upload and reading (R3). Files go to {root}/{itemId}/{imageId}.{ext},
/// with a thumbnail (400 px max) next to it as {imageId}_thumb.{ext}.
/// </summary>
public sealed class ImageManager(AuctionDbContext db, SellerGuard guard, ImageStoreOptions options, TimeProvider clock)
{
    public const int ThumbSize = 400;
    public const int MaxImagesPerItem = 10;
    public const long MaxPixels = 40_000_000;   // stops tiny files that unpack into huge pictures

    public async Task<ItemImage> UploadAsync(Guid sellerId, UploadHeader header, IAsyncEnumerable<ReadOnlyMemory<byte>> data, CancellationToken ct = default)
    {
        await guard.EnsureActiveSellerAsync(sellerId, ct);

        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ItemId == header.ItemId, ct)
                   ?? throw new DomainException(ErrorCodes.NotFound, "Item not found.");
        if (item.SellerId != sellerId)
        {
            throw new DomainException(ErrorCodes.NotOwner, "This is not your item.");
        }

        if (item.Status is ItemStatus.Sold or ItemStatus.Removed)
        {
            throw new DomainException(ErrorCodes.InvalidState, "You cannot add images to a sold or removed item.");
        }

        var declared = ImageSniffer.FromContentType(header.ContentType)
                       ?? throw Invalid("Only JPEG, PNG or WebP images are allowed.");
        if (header.Bytes <= 0 || header.Bytes > ImageSniffer.MaxBytes)
        {
            throw Invalid("Images must be 5 MB or smaller.");
        }

        var count = await db.ItemImages.CountAsync(i => i.ItemId == header.ItemId, ct);
        if (count >= MaxImagesPerItem)
        {
            throw Invalid($"An item can have at most {MaxImagesPerItem} images.");
        }

        var imageId = Guid.NewGuid();
        var folder = Path.Combine(options.RootPath, header.ItemId.ToString());
        var tempFolder = Path.Combine(options.RootPath, "tmp");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(tempFolder);
        var tempPath = Path.Combine(tempFolder, imageId + ".upload");

        try
        {
            var written = await WriteTempFileAsync(tempPath, declared, data, ct);
            var (width, height) = await MakeThumbnailAsync(tempPath, Path.Combine(folder, $"{imageId}_thumb.{declared.Extension}"), ct);

            var finalPath = Path.Combine(folder, $"{imageId}.{declared.Extension}");
            File.Move(tempPath, finalPath);

            var row = new ItemImage
            {
                ImageId = imageId,
                ItemId = header.ItemId,
                StorageKey = $"{header.ItemId}/{imageId}.{declared.Extension}",
                ContentType = declared.ContentType,
                Bytes = (int)written,
                Width = width,
                Height = height,
                SortOrder = (byte)count,
                Status = ImageStatus.Ready,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            db.ItemImages.Add(row);
            await db.SaveChangesAsync(ct);
            return row;
        }
        catch
        {
            DeleteQuietly(tempPath);
            DeleteQuietly(Path.Combine(folder, $"{imageId}.{declared.Extension}"));
            DeleteQuietly(Path.Combine(folder, $"{imageId}_thumb.{declared.Extension}"));
            throw;
        }
    }

    /// <summary>The file for an image, or null if there is none.</summary>
    public async Task<StoredImage?> FindAsync(Guid imageId, bool thumb, CancellationToken ct = default)
    {
        var row = await db.ItemImages.AsNoTracking()
            .FirstOrDefaultAsync(i => i.ImageId == imageId && i.Status == ImageStatus.Ready, ct);
        if (row == null)
        {
            return null;
        }

        var path = Path.Combine(options.RootPath, row.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        if (thumb)
        {
            path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_thumb" + Path.GetExtension(path));
        }

        return File.Exists(path) ? new StoredImage(path, row.ContentType) : null;
    }

    /// <summary>Removes every image file of an item (used when a draft is deleted).</summary>
    public void DeleteItemFiles(Guid itemId)
    {
        var folder = Path.Combine(options.RootPath, itemId.ToString());
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Streams to disk, counts bytes, and checks the magic bytes as soon as the first 12 arrive.
    private static async Task<long> WriteTempFileAsync(string tempPath, ImageKind declared, IAsyncEnumerable<ReadOnlyMemory<byte>> data, CancellationToken ct)
    {
        long written = 0;
        var head = new byte[ImageSniffer.BytesNeeded];
        var headLength = 0;
        var checkedHead = false;

        await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await foreach (var chunk in data.WithCancellation(ct))
            {
                written += chunk.Length;
                if (written > ImageSniffer.MaxBytes)
                {
                    throw Invalid("Images must be 5 MB or smaller.");
                }

                if (!checkedHead)
                {
                    var take = Math.Min(head.Length - headLength, chunk.Length);
                    chunk.Span[..take].CopyTo(head.AsSpan(headLength));
                    headLength += take;
                    if (headLength == head.Length)
                    {
                        EnsureKind(head, declared);
                        checkedHead = true;
                    }
                }

                await file.WriteAsync(chunk, ct);
            }
        }

        if (!checkedHead)
        {
            EnsureKind(head.AsSpan(0, headLength), declared);   // very small file
        }

        return written;
    }

    private static void EnsureKind(ReadOnlySpan<byte> head, ImageKind declared)
    {
        var actual = ImageSniffer.Detect(head);
        if (actual == null || actual != declared)
        {
            throw Invalid("The file is not a real JPEG, PNG or WebP image.");
        }
    }

    private static async Task<(int Width, int Height)> MakeThumbnailAsync(string sourcePath, string thumbPath, CancellationToken ct)
    {
        try
        {
            var info = await Image.IdentifyAsync(sourcePath, ct);
            if ((long)info.Width * info.Height > MaxPixels)
            {
                throw Invalid("The image is too large (too many pixels).");
            }

            var decoder = new DecoderOptions { MaxFrames = 1, TargetSize = new Size(ThumbSize, ThumbSize) };
            using var image = await Image.LoadAsync(decoder, sourcePath, ct);
            image.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(ThumbSize, ThumbSize) }));
            await image.SaveAsync(thumbPath, ct);

            return (info.Width, info.Height);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or ImageFormatException)
        {
            throw Invalid("The image file is damaged or not supported.");
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static DomainException Invalid(string message) => new(ErrorCodes.UploadInvalid, message);
}
