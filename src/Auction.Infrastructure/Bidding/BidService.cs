using System.Diagnostics;
using System.Diagnostics.Metrics;
using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Bidding;

/// <summary>A bid as the user sent it. The amount is in Money form (units + nanos).</summary>
public sealed record PlaceBidCommand(
    Guid UserId,
    string? AuctionId,
    long MaxUnits,
    int MaxNanos,
    string? Currency,
    string? IdempotencyKey,
    string? CorrelationId);

/// <summary>Bid counters and timings for Prometheus (meter "Auction.Bidding").</summary>
public static class BiddingMetrics
{
    public const string MeterName = "Auction.Bidding";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>bids_total{outcome}: Leading, OutbidByProxy, MaxRaised, or the error code.</summary>
    public static readonly Counter<long> Bids = Meter.CreateCounter<long>("bids_total");

    /// <summary>bid_latency_ms: time inside PlaceBid, from checks to the reply.</summary>
    public static readonly Histogram<double> Latency = Meter.CreateHistogram<double>("bid_latency_ms", "ms");
}

/// <summary>
/// The PlaceBid flow (spec 03 design 5):
/// check the input, the rate limit and the user's status, run bid.lua, then wait for a replica if asked.
/// </summary>
public sealed class BidService(
    BidScript script,
    IRateLimiter limiter,
    UserStatusCache statusCache,
    AuctionDbContext db,
    IConnectionMultiplexer redis,
    RedisOptions options,
    ILogger<BidService> log)
{
    public async Task<BidOutcome> PlaceAsync(PlaceBidCommand command, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var label = "error";
        try
        {
            var outcome = await PlaceCoreAsync(command, ct);
            label = outcome.Result!.Value.ToString();
            return outcome;
        }
        catch (DomainException ex)
        {
            label = ex.Code;
            throw;
        }
        finally
        {
            BiddingMetrics.Bids.Add(1, new KeyValuePair<string, object?>("outcome", label));
            BiddingMetrics.Latency.Record(clock.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<BidOutcome> PlaceCoreAsync(PlaceBidCommand command, CancellationToken ct)
    {
        // 1. Input (R3.4)
        var auctionId = ParseGuid(command.AuctionId, "auction_id");
        var idempotencyKey = ParseGuid(command.IdempotencyKey, "idempotency_key");
        var maxC = BidAmountRules.ToCents(command.MaxUnits, command.MaxNanos, command.Currency);
        var currency = command.Currency!.Trim().ToUpperInvariant();

        // 2. At most 5 bids a second per user (R1.4)
        var rate = await limiter.TryAcquireAsync(RateLimitPolicies.Bid, command.UserId.ToString());
        if (!rate.Allowed)
        {
            throw new DomainException(ErrorCodes.RateLimited, "You are bidding too fast. Please wait a moment.")
            {
                RetryAfterMs = rate.RetryAfterMs,
            };
        }

        // 3. Banned or suspended users never reach the script (R1.2)
        var status = await statusCache.GetAsync(command.UserId, ct)
                     ?? throw new DomainException(ErrorCodes.Unauthenticated, "Please sign in again.");
        if (status.Status == UserStatus.Banned)
        {
            throw new DomainException(ErrorCodes.AccountBanned, "This account is banned.");
        }

        if (status.Status == UserStatus.Suspended)
        {
            throw new DomainException(ErrorCodes.AccountSuspended, "This account is suspended.");
        }

        // 4. The bid itself, in one Redis script
        var keys = RedisKeys.For(auctionId, options);
        var fingerprint = BidAmountRules.Fingerprint(command.UserId, maxC, currency);
        BidOutcome outcome;
        try
        {
            outcome = await script.RunAsync(keys, command.UserId, maxC, fingerprint, idempotencyKey, currency, command.CorrelationId);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogWarning(ex, "bid.lua failed for auction {AuctionId}", auctionId);
            throw new DomainException(ErrorCodes.ServiceUnavailable, "Bidding is busy right now. Please try again.") { RetryAfterMs = 200 };
        }

        if (!outcome.Accepted)
        {
            await ThrowForAsync(outcome, auctionId, ct);
        }

        // 5. Durability: a replica must have it before we say yes (R8.1). A retry with the same key gets the stored outcome.
        if (options.RequireReplicaAck)
        {
            var replicas = (long)await redis.GetDatabase().ExecuteAsync("WAIT", 1, 50);
            if (replicas < 1)
            {
                log.LogWarning("No replica confirmed bid on auction {AuctionId} within 50 ms", auctionId);
                throw new DomainException(ErrorCodes.ServiceUnavailable, "Your bid could not be confirmed. Please try again.") { RetryAfterMs = 100 };
            }
        }

        return outcome;
    }

    private async Task ThrowForAsync(BidOutcome outcome, Guid auctionId, CancellationToken ct)
    {
        switch (outcome.ErrorCode)
        {
            case BidOutcome.Missing:
                // Not in Redis: never opened, already settled, or not loaded yet.
                var exists = await db.Auctions.AnyAsync(a => a.AuctionId == auctionId && a.Status != AuctionStatus.Draft, ct);
                throw exists
                    ? new DomainException(ErrorCodes.NotLive, "This auction is not open for bids.")
                    : new DomainException(ErrorCodes.NotFound, "Auction not found.");

            case BidOutcome.CurrencyMismatch:
                throw new DomainException(ErrorCodes.ValidationFailed, "The bid currency does not match the auction.",
                    [new FieldIssue("max_amount.currency", "match", "Use the auction's currency.")]);

            case BidOutcome.BadState:
                log.LogError("Auction {AuctionId} has a bad Redis state (no increment table)", auctionId);
                throw new DomainException(ErrorCodes.Internal, "This auction cannot take bids right now.");

            case ErrorCodes.BelowMinimum:
                throw new DomainException(ErrorCodes.BelowMinimum, "Your max bid is below the minimum next bid.") { MinNextBidC = outcome.MinNextC };

            case ErrorCodes.NotLive:
                throw new DomainException(ErrorCodes.NotLive, "This auction is not open for bids.");

            case ErrorCodes.Ended:
                throw new DomainException(ErrorCodes.Ended, "This auction has ended.");

            case ErrorCodes.AuctionFrozen:
                throw new DomainException(ErrorCodes.AuctionFrozen, "This auction is paused.");

            case ErrorCodes.OwnLot:
                throw new DomainException(ErrorCodes.OwnLot, "You cannot bid on your own auction.");

            case ErrorCodes.IdempotencyMismatch:
                throw new DomainException(ErrorCodes.IdempotencyMismatch, "This bid key was already used for a different bid.");

            default:
                throw new InvalidOperationException($"bid.lua returned an unknown code '{outcome.ErrorCode}'.");
        }
    }

    private static Guid ParseGuid(string? value, string field) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new DomainException(ErrorCodes.ValidationFailed, $"{field} is not a valid id.",
                [new FieldIssue(field, "format", "Must be a UUID.")]);
}
