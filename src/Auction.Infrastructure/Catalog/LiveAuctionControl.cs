namespace Auction.Infrastructure.Catalog;

/// <summary>
/// Talks to the live (Redis) copy of an auction. Spec 05 implements this with a Lua script that
/// sets the Redis status to Cancelled only if there are still 0 bids, so a bid and a cancel can
/// never both win (design 8).
/// </summary>
public interface ILiveAuctionControl
{
    /// <summary>True if the live auction was cancelled; false if a bid got in first.</summary>
    Task<bool> CancelAsync(Guid auctionId, CancellationToken ct = default);
}

/// <summary>Placeholder until spec 05: auctions are not loaded into Redis yet, so there is nothing to race.</summary>
public sealed class StubLiveAuctionControl : ILiveAuctionControl
{
    public Task<bool> CancelAsync(Guid auctionId, CancellationToken ct = default) => Task.FromResult(true);
}
