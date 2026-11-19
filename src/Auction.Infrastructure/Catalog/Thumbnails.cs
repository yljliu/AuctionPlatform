using Auction.Domain.Catalog;
using Auction.Domain.Errors;
using SkiaSharp;

namespace Auction.Infrastructure.Catalog;

/// <summary>
/// Makes thumbnails with SkiaSharp (MIT license). Works on bytes only, so it is unit tested
/// without a database or disk.
/// </summary>
public static class Thumbnails
{
    public const int JpegQuality = 85;

    /// <summary>
    /// Returns the picture's size and the thumbnail bytes (same format, fits in maxSide x maxSide,
    /// never made bigger). Throws UPLOAD_INVALID when the bytes are not a readable picture of that
    /// kind, or the picture has more than maxPixels pixels.
    /// </summary>
    public static (int Width, int Height, byte[] Thumb) Make(byte[] bytes, ImageKind kind, int maxSide, long maxPixels)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
                          ?? throw Invalid("The image file is damaged or not supported.");

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0)
        {
            throw Invalid("The image file is damaged or not supported.");
        }

        if (FormatOf(codec.EncodedFormat) != kind)
        {
            throw Invalid("The file is not a real JPEG, PNG or WebP image.");
        }

        // Checked before decoding, so a small file that unpacks into a huge picture never uses the memory.
        if ((long)width * height > maxPixels)
        {
            throw Invalid("The image is too large (too many pixels).");
        }

        using var full = Decode(codec);
        var (thumbWidth, thumbHeight) = FitInside(width, height, maxSide);

        using var small = thumbWidth == full.Width && thumbHeight == full.Height
            ? full.Copy()
            : full.Resize(new SKImageInfo(thumbWidth, thumbHeight, full.ColorType, full.AlphaType),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (small == null)
        {
            throw Invalid("The image file is damaged or not supported.");
        }

        using var image = SKImage.FromBitmap(small);
        using var encoded = image.Encode(EncoderFor(kind), JpegQuality)
                            ?? throw Invalid("The thumbnail could not be made.");
        return (width, height, encoded.ToArray());
    }

    /// <summary>The largest size with the same shape that fits in maxSide x maxSide. Never larger than the original.</summary>
    public static (int Width, int Height) FitInside(int width, int height, int maxSide)
    {
        if (width <= maxSide && height <= maxSide)
        {
            return (width, height);
        }

        var scale = (double)maxSide / Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static SKBitmap Decode(SKCodec codec)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw Invalid("The image file is damaged or not supported.");
        }

        return bitmap;
    }

    private static ImageKind? FormatOf(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => ImageSniffer.Jpeg,
        SKEncodedImageFormat.Png => ImageSniffer.Png,
        SKEncodedImageFormat.Webp => ImageSniffer.WebP,
        _ => null,
    };

    private static SKEncodedImageFormat EncoderFor(ImageKind kind) =>
        kind == ImageSniffer.Jpeg ? SKEncodedImageFormat.Jpeg
        : kind == ImageSniffer.WebP ? SKEncodedImageFormat.Webp
        : SKEncodedImageFormat.Png;

    private static DomainException Invalid(string message) => new(ErrorCodes.UploadInvalid, message);
}
