namespace Auction.Domain.Catalog;

public sealed record ImageKind(string ContentType, string Extension);

/// <summary>
/// Finds the real file type from its first bytes ("magic bytes"), not from the name or what the
/// browser claims. Only JPEG, PNG and WebP are allowed (R3.2).
/// </summary>
public static class ImageSniffer
{
    public const int BytesNeeded = 12;
    public const long MaxBytes = 5 * 1024 * 1024;

    public static readonly ImageKind Jpeg = new("image/jpeg", "jpg");
    public static readonly ImageKind Png = new("image/png", "png");
    public static readonly ImageKind WebP = new("image/webp", "webp");

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Null when the bytes are not one of the allowed types.</summary>
    public static ImageKind? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return Jpeg;
        }

        if (head.Length >= PngSignature.Length && head[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return Png;
        }

        // RIFF....WEBP
        if (head.Length >= BytesNeeded
            && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
            && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
        {
            return WebP;
        }

        return null;
    }

    /// <summary>The kind for a declared content type, or null if it is not allowed.</summary>
    public static ImageKind? FromContentType(string? contentType) =>
        contentType?.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => Jpeg,
            "image/png" => Png,
            "image/webp" => WebP,
            _ => null,
        };
}
