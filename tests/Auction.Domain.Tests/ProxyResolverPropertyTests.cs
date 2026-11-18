using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using FsCheck;
using FsCheck.Xunit;

namespace Auction.Domain.Tests;

/// <summary>
/// Spec 03 task 2: FsCheck tries hundreds of random bid sequences and checks rules that must always hold.
/// Users are 0 to 3, maxes are 1 cent to 100,000.00, the start is 1 cent to 1,000.00.
/// </summary>
public class ProxyResolverPropertyTests
{
    private static readonly IncrementTable Increments = IncrementTable.Parse(ProxyResolverVectorTests.Table1);

    private static readonly Guid[] People =
    [
        Guid.Parse("00000000-0000-0000-0000-00000000000a"),
        Guid.Parse("00000000-0000-0000-0000-00000000000b"),
        Guid.Parse("00000000-0000-0000-0000-00000000000c"),
        Guid.Parse("00000000-0000-0000-0000-00000000000d"),
    ];

    private sealed record Step(BidState Before, Guid User, long MaxC, Resolution? Result, long? MinNextC);

    /// <summary>Plays the bids in order from an empty auction. Rejected bids leave the state as it was.</summary>
    private static List<Step> Play(int startSeed, byte[] users, int[] maxes)
    {
        var state = new BidState(0, null, 0, 0, 1 + Math.Abs(startSeed % 100_000));
        var steps = new List<Step>();
        users ??= [];
        maxes ??= [];
        var count = Math.Min(users.Length, maxes.Length);

        for (var i = 0; i < count; i++)
        {
            var user = People[users[i] % People.Length];
            var maxC = 1 + Math.Abs(maxes[i] % 10_000_000);
            try
            {
                var result = ProxyResolver.Resolve(state, user, maxC, Increments);
                steps.Add(new Step(state, user, maxC, result, null));
                state = result.Next;
            }
            catch (DomainException ex) when (ex.Code == ErrorCodes.BelowMinimum)
            {
                steps.Add(new Step(state, user, maxC, null, ex.MinNextBidC));
            }
        }

        return steps;
    }

    [Property(MaxTest = 500)]
    public bool Price_never_goes_down_and_never_passes_the_leaders_max(int start, byte[] users, int[] maxes)
    {
        foreach (var step in Play(start, users, maxes).Where(s => s.Result != null))
        {
            var next = step.Result!.Next;
            if (next.PriceC < step.Before.PriceC || next.PriceC > next.LeaderMaxC || next.PriceC < next.StartC)
            {
                return false;
            }
        }

        return true;
    }

    [Property(MaxTest = 500)]
    public bool Nobody_ever_pays_more_than_their_own_max(int start, byte[] users, int[] maxes)
    {
        foreach (var step in Play(start, users, maxes).Where(s => s.Result != null))
        {
            foreach (var row in step.Result!.Rows)
            {
                // The new leader's row is at most their max; the old leader's proxy row is at most theirs.
                var limit = row.User == step.User ? step.MaxC : step.Before.LeaderMaxC;
                if (row.AmountC > limit)
                {
                    return false;
                }
            }
        }

        return true;
    }

    [Property(MaxTest = 500)]
    public bool The_leader_always_holds_the_highest_accepted_max(int start, byte[] users, int[] maxes)
    {
        var best = new Dictionary<Guid, long>();
        BidState? last = null;

        foreach (var step in Play(start, users, maxes).Where(s => s.Result != null))
        {
            best[step.User] = Math.Max(best.GetValueOrDefault(step.User), step.MaxC);
            last = step.Result!.Next;

            if (last.LeaderMaxC != best.Values.Max() || best[last.Leader!.Value] != last.LeaderMaxC)
            {
                return false;
            }
        }

        return true;
    }

    [Property(MaxTest = 500)]
    public bool A_rejected_bid_is_always_below_the_min_next_it_reports(int start, byte[] users, int[] maxes)
    {
        foreach (var step in Play(start, users, maxes).Where(s => s.Result == null))
        {
            if (step.MinNextC == null || step.MaxC >= step.MinNextC)
            {
                return false;
            }

            // Bidding exactly that min next must then be accepted.
            try
            {
                ProxyResolver.Resolve(step.Before, step.User, step.MinNextC.Value, Increments);
            }
            catch (DomainException)
            {
                return false;
            }
        }

        return true;
    }

    [Property(MaxTest = 500)]
    public bool Results_match_their_rows_and_counts(int start, byte[] users, int[] maxes)
    {
        foreach (var step in Play(start, users, maxes).Where(s => s.Result != null))
        {
            var result = step.Result!;
            var ok = result.Result switch
            {
                BidResult.Leading => result.Next.Leader == step.User
                                     && result.Next.LeaderMaxC == step.MaxC
                                     && result.Next.BidCount == step.Before.BidCount + 1
                                     && result.Rows[^1] == new BidRow(step.User, result.Next.PriceC, BidKind.Manual),
                BidResult.OutbidByProxy => result.Next.Leader == step.Before.Leader
                                           && result.Next.PriceC >= step.MaxC
                                           && result.Next.BidCount == step.Before.BidCount + 1
                                           && result.Rows.Count == 2
                                           && result.Rows[0].Kind == BidKind.AutoOutbid,
                BidResult.MaxRaised => result.Next.PriceC == step.Before.PriceC
                                       && result.Next.BidCount == step.Before.BidCount
                                       && result.Rows.Single().Kind == BidKind.MaxRaise,
                _ => false,
            };

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    [Property(MaxTest = 300)]
    public bool Same_input_always_gives_the_same_answer(int start, byte[] users, int[] maxes)
    {
        var first = Play(start, users, maxes);
        var second = Play(start, users, maxes);

        return first.Count == second.Count
               && first.Zip(second).All(p => p.First.Result?.Next == p.Second.Result?.Next
                                             && p.First.MinNextC == p.Second.MinNextC
                                             && (p.First.Result == null || p.First.Result.Rows.SequenceEqual(p.Second.Result!.Rows)));
    }
}
