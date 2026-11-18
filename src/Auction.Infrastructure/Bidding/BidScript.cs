using Auction.Infrastructure.Redis;
using StackExchange.Redis;

namespace Auction.Infrastructure.Bidding;

/// <summary>Calls bid.lua with the right keys and arguments, and reads its answer.</summary>
public sealed class BidScript(ScriptRegistry scripts)
{
    public const string FileName = "bid.lua";

    public async Task<BidOutcome> RunAsync(AuctionKeys keys, Guid userId, long maxC, string fingerprint,
        Guid idempotencyKey, string currency, string? correlationId)
    {
        RedisKey[] keyList =
        [
            keys.State, keys.Max, keys.Bids, keys.Events, keys.Idempotency(idempotencyKey.ToString()), keys.Stream,
        ];
        RedisValue[] args =
        [
            userId.ToString(), maxC, fingerprint, correlationId ?? "", idempotencyKey.ToString(), keys.AuctionId.ToString(),
            currency,
        ];

        var result = await scripts.EvaluateAsync(FileName, keyList, args);
        var parts = (RedisResult[]?)result
                    ?? throw new FormatException("bid.lua returned nothing.");
        if (parts.Length != 2)
        {
            throw new FormatException($"bid.lua returned {parts.Length} values; expected 2.");
        }

        return BidOutcome.Parse((string?)parts[0], (long)parts[1] == 1);
    }
}
