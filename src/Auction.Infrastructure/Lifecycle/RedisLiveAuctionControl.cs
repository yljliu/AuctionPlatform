using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Redis;
using StackExchange.Redis;

namespace Auction.Infrastructure.Lifecycle;

/// <summary>The real ILiveAuctionControl: runs control.lua on the auction's Redis keys (spec 05 task 5).</summary>
public sealed class RedisLiveAuctionControl(LifecycleScripts scripts, IConnectionMultiplexer redis, RedisOptions options) : ILiveAuctionControl
{
    public Task<LiveControlOutcome> CancelAsync(Guid auctionId, bool force, CancellationToken ct = default) =>
        RunAsync(auctionId, ControlOp.Cancel, force);

    public Task<LiveControlOutcome> CloseAsync(Guid auctionId, CancellationToken ct = default) =>
        RunAsync(auctionId, ControlOp.Close, false);

    public Task<LiveControlOutcome> FreezeAsync(Guid auctionId, CancellationToken ct = default) =>
        RunAsync(auctionId, ControlOp.Freeze, false);

    public Task<LiveControlOutcome> ResumeAsync(Guid auctionId, CancellationToken ct = default) =>
        RunAsync(auctionId, ControlOp.Resume, false);

    private async Task<LiveControlOutcome> RunAsync(Guid auctionId, ControlOp op, bool force)
    {
        var keys = RedisKeys.For(auctionId, options);
        var result = await scripts.ControlAsync(keys, op, force);
        var mapped = Map(result);
        if (mapped != LiveControlResult.Done)
        {
            return new LiveControlOutcome(mapped);
        }

        var end = await redis.GetDatabase().HashGetAsync(keys.State, "endMs");
        return new LiveControlOutcome(mapped, end.TryParse(out long endMs) ? endMs : null);
    }

    public static LiveControlResult Map(LifecycleResult result) => result switch
    {
        LifecycleResult.Done => LiveControlResult.Done,
        LifecycleResult.Missing => LiveControlResult.NotInRedis,
        _ => LiveControlResult.InvalidState,
    };
}
