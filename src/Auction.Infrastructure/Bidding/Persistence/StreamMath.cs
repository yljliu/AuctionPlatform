using System.Globalization;

namespace Auction.Infrastructure.Bidding.Persistence;

/// <summary>A Redis stream id: "milliseconds-sequence", for example "1763400000000-3".</summary>
public readonly record struct StreamId(long Ms, long Seq) : IComparable<StreamId>
{
    public static StreamId Parse(string text)
    {
        var dash = text.IndexOf('-');
        if (dash <= 0
            || !long.TryParse(text.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            || !long.TryParse(text.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
        {
            throw new FormatException($"'{text}' is not a stream id.");
        }

        return new StreamId(ms, seq);
    }

    public static StreamId? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            return Parse(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public int CompareTo(StreamId other) => Ms != other.Ms ? Ms.CompareTo(other.Ms) : Seq.CompareTo(other.Seq);

    public static bool operator <(StreamId a, StreamId b) => a.CompareTo(b) < 0;

    public static bool operator >(StreamId a, StreamId b) => a.CompareTo(b) > 0;

    public override string ToString() => $"{Ms}-{Seq}";
}

/// <summary>What one consumer group still needs from the stream.</summary>
public sealed record GroupPosition(string Name, string? LowestPendingId, string? LastDeliveredId);

/// <summary>Small pure rules for the persister: trimming, lag and retry delays.</summary>
public static class StreamMath
{
    public static readonly TimeSpan KeepAtLeast = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The MINID for XTRIM, or null to skip trimming (spec 03 design 6). Entries below it are gone.
    /// It is the lowest id any group still needs (its oldest pending entry, else its last delivered one),
    /// and never later than "now minus 10 minutes".
    /// </summary>
    public static string? TrimMinId(IReadOnlyList<GroupPosition> groups, long nowMs)
    {
        if (groups.Count == 0)
        {
            return null;   // nobody reads it yet: keep everything
        }

        StreamId? lowest = null;
        foreach (var group in groups)
        {
            var needed = StreamId.TryParse(group.LowestPendingId) ?? StreamId.TryParse(group.LastDeliveredId);
            if (needed == null)
            {
                return null;   // a group that has read nothing yet needs the whole stream
            }

            if (lowest == null || needed.Value < lowest.Value)
            {
                lowest = needed;
            }
        }

        var keepFrom = new StreamId(nowMs - (long)KeepAtLeast.TotalMilliseconds, 0);
        var minId = lowest!.Value < keepFrom ? lowest.Value : keepFrom;
        return minId.Ms <= 0 ? null : minId.ToString();
    }

    /// <summary>
    /// persist_lag_ms: age of the oldest entry SQL does not have yet, from the oldest pending id
    /// or the first entry not yet read. 0 when everything is written.
    /// </summary>
    public static long LagMs(long nowMs, string? lowestPendingId, string? firstUndeliveredId)
    {
        var a = StreamId.TryParse(lowestPendingId);
        var b = StreamId.TryParse(firstUndeliveredId);
        var oldest = (a, b) switch
        {
            (null, null) => (StreamId?)null,
            (null, _) => b,
            (_, null) => a,
            _ => a.Value < b!.Value ? a : b,
        };

        return oldest == null ? 0 : Math.Max(0, nowMs - oldest.Value.Ms);
    }

    /// <summary>Wait after the n-th failure in a row (1 = first): 100 ms, 200, 400 ... at most 5 s.</summary>
    public static TimeSpan Backoff(int failuresInARow)
    {
        if (failuresInARow <= 0)
        {
            return TimeSpan.Zero;
        }

        var ms = 100.0 * Math.Pow(2, Math.Min(failuresInARow - 1, 16));
        return TimeSpan.FromMilliseconds(Math.Min(ms, 5000));
    }
}
