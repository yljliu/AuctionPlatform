using System.Globalization;
using Auction.Domain.Bidding;

namespace Auction.Infrastructure.Bidding;

/// <summary>
/// What bid.lua answered, read from its outcome string:
/// "ok|Result|priceC|minNextC|seq|endMs|extended|bidCount|isLeader" or "err|CODE|minNextC".
/// </summary>
public sealed record BidOutcome(
    bool Accepted,
    BidResult? Result,
    string? ErrorCode,
    long PriceC,
    long MinNextC,
    long Seq,
    long EndMs,
    bool Extended,
    int BidCount,
    bool IsLeader,
    bool Replayed)
{
    /// <summary>Codes only bid.lua uses; the Api turns them into real error codes or SQL checks.</summary>
    public const string Missing = "MISSING";
    public const string CurrencyMismatch = "CURRENCY";
    public const string BadState = "BAD_STATE";

    public static BidOutcome Parse(string? text, bool replayed)
    {
        var parts = (text ?? "").Split('|');

        if (parts.Length == 3 && parts[0] == "err" && parts[1].Length > 0)
        {
            return new BidOutcome(false, null, parts[1], 0, Long(parts[2], text), 0, 0, false, 0, false, replayed);
        }

        if (parts.Length == 9 && parts[0] == "ok" && Enum.TryParse<BidResult>(parts[1], ignoreCase: false, out var result)
            && Enum.IsDefined(result))
        {
            return new BidOutcome(
                Accepted: true,
                Result: result,
                ErrorCode: null,
                PriceC: Long(parts[2], text),
                MinNextC: Long(parts[3], text),
                Seq: Long(parts[4], text),
                EndMs: Long(parts[5], text),
                Extended: Flag(parts[6], text),
                BidCount: (int)Long(parts[7], text),
                IsLeader: Flag(parts[8], text),
                Replayed: replayed);
        }

        throw new FormatException($"bid.lua returned an outcome that could not be read: '{text}'.");
    }

    private static long Long(string value, string? text) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"bid.lua returned a bad number '{value}' in '{text}'.");

    private static bool Flag(string value, string? text) => value switch
    {
        "1" => true,
        "0" => false,
        _ => throw new FormatException($"bid.lua returned a bad flag '{value}' in '{text}'."),
    };
}
