using Auction.Domain.Catalog;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

public class ImageSnifferTests
{
    [Fact]
    public void Detects_jpeg()
    {
        ImageSniffer.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0]).Should().Be(ImageSniffer.Jpeg);
    }

    [Fact]
    public void Detects_png()
    {
        ImageSniffer.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]).Should().Be(ImageSniffer.Png);
    }

    [Fact]
    public void Detects_webp()
    {
        ImageSniffer.Detect("RIFF\0\0\0\0WEBP"u8).Should().Be(ImageSniffer.WebP);
    }

    [Fact]
    public void Windows_exe_is_not_an_image()
    {
        ImageSniffer.Detect("MZ\x90\0\x03\0\0\0\x04\0\0\0"u8).Should().BeNull();
    }

    [Fact]
    public void Riff_that_is_not_webp_is_refused()
    {
        ImageSniffer.Detect("RIFF\0\0\0\0WAVE"u8).Should().BeNull();   // a sound file
    }

    [Fact]
    public void Too_few_bytes_is_refused()
    {
        ImageSniffer.Detect([0x89, 0x50]).Should().BeNull();
    }

    [Theory]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("IMAGE/PNG", "png")]
    [InlineData("image/webp", "webp")]
    public void Allowed_content_types(string contentType, string extension)
    {
        ImageSniffer.FromContentType(contentType)!.Extension.Should().Be(extension);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_content_types_are_refused(string? contentType)
    {
        ImageSniffer.FromContentType(contentType).Should().BeNull();
    }
}
