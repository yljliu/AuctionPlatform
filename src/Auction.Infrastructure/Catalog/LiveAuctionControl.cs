namespace Auction.Infrastructure.Catalog;

public enum LiveControlResult
{
    /// <summary>The live (Redis) copy changed.</summary>
    Done,

    /// <summary>The auction is not in Redis (not loaded yet, or already gone). SQL alone decides.</summary>
    NotInRedis,

    /// <summary>The live copy refused: wrong status, or (seller cancel) a bid got in first.</summary>
    InvalidState,
}

/// <param name="EndMs">The end time after the action (resume pushes it later), when known.</param>
public sealed record LiveControlOutcome(LiveControlResult Result, long? EndMs = null);

/// <summary>
/// Talks to the live (Redis) copy of an auction (spec 05 design 8). Each call is one Lua script that
/// checks the status first, so a bid and a cancel can never both win (R6.1).
/// </summary>
public interface ILiveAuctionControl
{
    /// <param name="force">Admin force-cancel: cancel even when bids exist (R6.2).</param>
    Task<LiveControlOutcome> CancelAsync(Guid auctionId, bool force, CancellationToken ct = default);

    /// <summary>Set the end time to now; the normal seal path runs next (R6.3).</summary>
    Task<LiveControlOutcome> CloseAsync(Guid auctionId, CancellationToken ct = default);

    Task<LiveControlOutcome> FreezeAsync(Guid auctionId, CancellationToken ct = default);

    /// <summary>Live again; the end moves later by the time spent frozen (R6.4).</summary>
    Task<LiveControlOutcome> ResumeAsync(Guid auctionId, CancellationToken ct = default);
}

/// <summary>For tests that need no Redis: the auction is never in Redis.</summary>
public sealed class StubLiveAuctionControl : ILiveAuctionControl
{
    private static readonly Task<LiveControlOutcome> None = Task.FromResult(new LiveControlOutcome(LiveControlResult.NotInRedis));

    public Task<LiveControlOutcome> CancelAsync(Guid auctionId, bool force, CancellationToken ct = default) => None;

    public Task<LiveControlOutcome> CloseAsync(Guid auctionId, CancellationToken ct = default) => None;

    public Task<LiveControlOutcome> FreezeAsync(Guid auctionId, CancellationToken ct = default) => None;

    public Task<LiveControlOutcome> ResumeAsync(Guid auctionId, CancellationToken ct = default) => None;
}
