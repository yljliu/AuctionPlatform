namespace Auction.Infrastructure.Live;

public enum StartKind
{
    /// <summary>Send a full snapshot first.</summary>
    Snapshot,

    /// <summary>Send the kept events after last_seen_seq.</summary>
    Replay,
}

public enum LiveStep
{
    /// <summary>Send it.</summary>
    Send,

    /// <summary>Already sent (catch-up and live overlap): drop it.</summary>
    Skip,

    /// <summary>A seq is missing: send a fresh snapshot instead.</summary>
    Resync,
}

/// <summary>
/// The no-gap, no-repeat rules of SubscribeAuction (spec 04 design 2.3, R1.1 to R1.3). Pure.
/// </summary>
public static class LiveSequencer
{
    /// <summary>
    /// Snapshot or replay? Replay only when the client has seen something, Redis holds the auction,
    /// the client is not ahead, and every event after last_seen is still kept.
    /// </summary>
    /// <param name="currentSeq">The state hash seq, or null when Redis does not hold the auction.</param>
    /// <param name="oldestKeptSeq">The lowest seq in the events ZSET, or null when it is empty.</param>
    public static StartKind ChooseStart(long lastSeen, long? currentSeq, long? oldestKeptSeq)
    {
        if (lastSeen <= 0 || currentSeq == null || lastSeen > currentSeq)
        {
            return StartKind.Snapshot;
        }

        if (lastSeen == currentSeq)
        {
            return StartKind.Replay;   // nothing missed: go straight to live
        }

        // Events lastSeen+1 .. current must all still be kept.
        return oldestKeptSeq != null && oldestKeptSeq <= lastSeen + 1 ? StartKind.Replay : StartKind.Snapshot;
    }

    /// <summary>What to do with the next event, given the last seq sent to this client.</summary>
    public static LiveStep Next(long sent, long eventSeq)
    {
        if (eventSeq <= sent)
        {
            return LiveStep.Skip;
        }

        return eventSeq == sent + 1 ? LiveStep.Send : LiveStep.Resync;
    }

    /// <summary>True when a replayed list starts right after last_seen and has no holes.</summary>
    public static bool IsUnbroken(long lastSeen, IReadOnlyList<long> seqs)
    {
        var expected = lastSeen + 1;
        foreach (var seq in seqs)
        {
            if (seq != expected)
            {
                return false;
            }

            expected++;
        }

        return true;
    }
}
