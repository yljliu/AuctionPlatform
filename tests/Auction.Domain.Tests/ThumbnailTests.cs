using Auction.Domain.Catalog;
using Auction.Domain.Errors;
using Auction.Infrastructure.Catalog;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Thumbnails with SkiaSharp, on bytes only (no disk, no database).</summary>
public class ThumbnailTests
{
    public static byte[] Picture(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(200, 30, 30));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static (int Width, int Height, SKEncodedImageFormat Format) Read(byte[] bytes)
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(bytes))!;
        return (codec.Info.Width, codec.Info.Height, codec.EncodedFormat);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "jpg")]
    [InlineData(SKEncodedImageFormat.Webp, "webp")]
    public void Wide_picture_gets_a_400_wide_thumbnail_in_the_same_format(SKEncodedImageFormat format, string extension)
    {
        var kind = new[] { ImageSniffer.Png, ImageSniffer.Jpeg, ImageSniffer.WebP }.Single(k => k.Extension == extension);

        var (width, height, thumb) = Thumbnails.Make(Picture(1200, 800, format), kind, 400, 40_000_000);

        (width, height).Should().Be((1200, 800));
        Read(thumb).Should().Be((400, 267, format));
    }

    [Fact]
    public void Tall_picture_is_limited_by_its_height()
    {
        var (_, _, thumb) = Thumbnails.Make(Picture(300, 900, SKEncodedImageFormat.Png), ImageSniffer.Png, 400, 40_000_000);

        Read(thumb).Should().Be((133, 400, SKEncodedImageFormat.Png));
    }

    [Fact]
    public void Small_picture_is_never_made_bigger()
    {
        var (_, _, thumb) = Thumbnails.Make(Picture(120, 80, SKEncodedImageFormat.Png), ImageSniffer.Png, 400, 40_000_000);

        Read(thumb).Should().Be((120, 80, SKEncodedImageFormat.Png));
    }

    [Fact]
    public void Too_many_pixels_is_UPLOAD_INVALID()
    {
        var act = () => Thumbnails.Make(Picture(2000, 2000, SKEncodedImageFormat.Png), ImageSniffer.Png, 400, maxPixels: 3_999_999);

        act.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.UploadInvalid);
    }

    [Fact]
    public void Jpeg_bytes_declared_as_png_is_UPLOAD_INVALID()
    {
        var act = () => Thumbnails.Make(Picture(50, 50, SKEncodedImageFormat.Jpeg), ImageSniffer.Png, 400, 40_000_000);

        act.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.UploadInvalid);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 })]   // PNG start, then junk
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public void Damaged_file_is_UPLOAD_INVALID(byte[] bytes)
    {
        var act = () => Thumbnails.Make(bytes, ImageSniffer.Png, 400, 40_000_000);

        act.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.UploadInvalid);
    }

    [Theory]
    [InlineData(1200, 800, 400, 400, 267)]
    [InlineData(800, 1200, 400, 267, 400)]
    [InlineData(400, 400, 400, 400, 400)]
    [InlineData(100, 50, 400, 100, 50)]
    [InlineData(5000, 1, 400, 400, 1)]
    public void Fit_inside_keeps_the_shape(int w, int h, int max, int ew, int eh)
    {
        Thumbnails.FitInside(w, h, max).Should().Be((ew, eh));
    }
}
