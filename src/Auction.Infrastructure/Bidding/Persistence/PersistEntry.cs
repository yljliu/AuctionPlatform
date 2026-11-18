using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Auction.Domain.Enums;

namespace Auction.Infrastructure.Bidding.Persistence;

/// <summary>One row inside a stream entry ("rows" JSON from bid.lua).</summary>
public sealed record PersistRow(
    [property: JsonPropertyName("user")] Guid User,
    [property: JsonPropertyName("amountC")] long AmountC,
    [property: JsonPropertyName("kind")] BidKind Kind);

public enum PersistEntryType
{
    /// <summary>A bid from bid.lua.</summary>
    Bid,

    /// <summary>A status change (freeze now; open, seal, settle in spec 05).</summary>
    State,
}

/// <summary>A stream entry read back from {pN}:stream, ready to write to SQL.</summary>
public sealed record PersistEntry(
    string Id,
    PersistEntryType Type,
    Guid AuctionId,
    long ESeq,
    long? Seq,
    long AtMs,
    // Bid only
    Guid? User = null,
    long MaxC = 0,
    string? Result = null,
    long PriceC = 0,
    Guid? Leader = null,
    int BidCount = 0,
    long EndMs = 0,
    bool Extended = false,
    IReadOnlyList<PersistRow>? Rows = null,
    Guid? IdempotencyKey = null,
    // State only
    string? Status = null);

/// <summary>
/// Reads the fields bid.lua and freeze.lua write with XADD. Throws FormatException for anything
/// that cannot be read; such an entry can never be written and goes to the dead-letter stream.
/// </summary>
public static class PersistEntryParser
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static PersistEntry Parse(string id, IReadOnlyDictionary<string, string> fields)
    {
        var type = Required(fields, "type", id) switch
        {
            "Bid" => PersistEntryType.Bid,
            "State" => PersistEntryType.State,
            var other => throw new FormatException($"Stream entry {id} has an unknown type '{other}'."),
        };

        var auctionId = GuidField(fields, "auction", id);
        var eseq = LongField(fields, "eseq", id);
        if (eseq <= 0)
        {
            throw new FormatException($"Stream entry {id} has eseq {eseq}; it must be 1 or more.");
        }

        var seqText = Required(fields, "seq", id);
        long? seq = seqText == "-" ? null : LongField(fields, "seq", id);
        var at = LongField(fields, "at", id);

        if (type == PersistEntryType.State)
        {
            return new PersistEntry(id, type, auctionId, eseq, seq, at, Status: Required(fields, "status", id));
        }

        var rowsText = Required(fields, "rows", id);
        List<PersistRow>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<PersistRow>>(rowsText, Json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Stream entry {id} has rows that are not valid JSON.", ex);
        }

        if (rows == null || rows.Count == 0 || rows.Count > byte.MaxValue)
        {
            throw new FormatException($"Stream entry {id} must have 1 to 255 rows.");
        }

        var result = Required(fields, "result", id);
        if (result is not ("Leading" or "OutbidByProxy" or "MaxRaised"))
        {
            throw new FormatException($"Stream entry {id} has an unknown result '{result}'.");
        }

        if (seq == null && result != "MaxRaised")
        {
            throw new FormatException($"Stream entry {id} changes the price but has no seq.");
        }

        Guid? idem = fields.TryGetValue("idem", out var idemText) && Guid.TryParse(idemText, out var parsedIdem) ? parsedIdem : null;

        return new PersistEntry(
            id, type, auctionId, eseq, seq, at,
            User: GuidField(fields, "user", id),
            MaxC: LongField(fields, "maxC", id),
            Result: result,
            PriceC: LongField(fields, "priceC", id),
            Leader: GuidField(fields, "leader", id),
            BidCount: (int)LongField(fields, "bidCount", id),
            EndMs: LongField(fields, "endMs", id),
            Extended: Required(fields, "extended", id) == "1",
            Rows: rows,
            IdempotencyKey: idem);
    }

    private static string Required(IReadOnlyDictionary<string, string> fields, string name, string id) =>
        fields.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : throw new FormatException($"Stream entry {id} has no '{name}'.");

    private static long LongField(IReadOnlyDictionary<string, string> fields, string name, string id) =>
        long.TryParse(Required(fields, name, id), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"Stream entry {id} has a bad number in '{name}'.");

    private static Guid GuidField(IReadOnlyDictionary<string, string> fields, string name, string id) =>
        Guid.TryParse(Required(fields, name, id), out var value)
            ? value
            : throw new FormatException($"Stream entry {id} has a bad id in '{name}'.");
}
