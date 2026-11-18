using Auction.Domain.Enums;
using Auction.Domain.Errors;

namespace Auction.Domain.Bidding;

public enum BidResult
{
    Leading,
    OutbidByProxy,
    MaxRaised,
}

/// <summary>
/// The bidding part of an auction's state, all money in cents.
/// Leader is null and BidCount is 0 before the first bid.
/// </summary>
public sealed record BidState(long PriceC, Guid? Leader, long LeaderMaxC, int BidCount, long StartC);

/// <summary>One bid history row a bid produces. MaxRaise rows are private (never shown to others).</summary>
public sealed record BidRow(Guid User, long AmountC, BidKind Kind);

/// <summary>What a bid did: the result, the new state, the rows to record, and who lost the lead (if anyone).</summary>
public sealed record Resolution(BidResult Result, BidState Next, IReadOnlyList<BidRow> Rows, Guid? Outbid);

/// <summary>
/// Proxy bidding rules (spec 03 design 3, R4). Each bid is a max: the system bids for you,
/// one increment at a time, up to your max. bid.lua (task 4) must give the same answer for every
/// case in tests/vectors/proxy.json.
///
/// Rules, checked in this order:
///   1. No bids yet: max must be at least the start; you lead at the start price.
///   2. You already lead: a higher max is stored (MaxRaised), the price does not move.
///   3. Otherwise max must be at least price + increment.
///   4. Max above the leader's max: you lead at min(your max, leader max + increment).
///   5. Max at or below the leader's max: the leader stays, at min(leader max, your max + increment).
///      On a tie the earlier leader wins.
/// </summary>
public static class ProxyResolver
{
    /// <summary>Throws DomainException(BELOW_MINIMUM) with MinNextBidC when the max is too low.</summary>
    public static Resolution Resolve(BidState state, Guid user, long maxC, IncrementTable increments)
    {
        CheckState(state);

        // 1. First bid
        if (state.BidCount == 0)
        {
            if (maxC < state.StartC)
            {
                throw BelowMinimum(state.StartC);
            }

            return new Resolution(
                BidResult.Leading,
                state with { PriceC = state.StartC, Leader = user, LeaderMaxC = maxC, BidCount = 1 },
                [new BidRow(user, state.StartC, BidKind.Manual)],
                Outbid: null);
        }

        var leader = state.Leader!.Value;

        // 2. The leader raises their own max
        if (user == leader)
        {
            if (maxC <= state.LeaderMaxC)
            {
                throw BelowMinimum(state.LeaderMaxC + 1);
            }

            return new Resolution(
                BidResult.MaxRaised,
                state with { LeaderMaxC = maxC },
                [new BidRow(user, state.PriceC, BidKind.MaxRaise)],
                Outbid: null);
        }

        // 3. Must beat the current price by at least one increment
        var minNext = increments.MinNext(state.PriceC, state.BidCount, state.StartC);
        if (maxC < minNext)
        {
            throw BelowMinimum(minNext);
        }

        // 4. The challenger takes the lead
        if (maxC > state.LeaderMaxC)
        {
            // Never below minNext, even with an odd table whose increments go down.
            var target = Math.Max(minNext, state.LeaderMaxC + increments.IncrementFor(state.LeaderMaxC));
            var newPrice = Math.Min(maxC, target);

            var rows = new List<BidRow>();
            if (state.LeaderMaxC > state.PriceC)
            {
                rows.Add(new BidRow(leader, state.LeaderMaxC, BidKind.ProxyAuto));   // old leader's proxy went to its max
            }

            rows.Add(new BidRow(user, newPrice, BidKind.Manual));

            return new Resolution(
                BidResult.Leading,
                state with { PriceC = newPrice, Leader = user, LeaderMaxC = maxC, BidCount = state.BidCount + 1 },
                rows,
                Outbid: leader);
        }

        // 5. The leader's proxy answers and keeps the lead
        var answer = Math.Min(state.LeaderMaxC, maxC + increments.IncrementFor(maxC));
        return new Resolution(
            BidResult.OutbidByProxy,
            state with { PriceC = answer, BidCount = state.BidCount + 1 },
            [new BidRow(user, maxC, BidKind.AutoOutbid), new BidRow(leader, answer, BidKind.ProxyAuto)],
            Outbid: null);
    }

    private static void CheckState(BidState state)
    {
        if (state.StartC <= 0)
        {
            throw new ArgumentException("Starting price must be more than 0.", nameof(state));
        }

        if (state.BidCount < 0)
        {
            throw new ArgumentException("Bid count cannot be negative.", nameof(state));
        }

        if (state.BidCount == 0 && state.Leader != null)
        {
            throw new ArgumentException("An auction with no bids cannot have a leader.", nameof(state));
        }

        if (state.BidCount > 0 && (state.Leader == null || state.LeaderMaxC < state.PriceC))
        {
            throw new ArgumentException("An auction with bids needs a leader whose max is at least the price.", nameof(state));
        }
    }

    private static DomainException BelowMinimum(long minNextC) =>
        new(ErrorCodes.BelowMinimum, "Your max bid is below the minimum next bid.") { MinNextBidC = minNextC };
}
