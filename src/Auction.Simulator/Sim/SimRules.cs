using Auction.Domain.Bidding;

namespace Auction.Simulator.Sim;

/// <summary>The three load shapes (spec 08 design 1, R2.1 to R2.3).</summary>
public static class Scenarios
{
    public const string Steady = "steady";
    public const string ClosingSpike = "closing-spike";
    public const string SnipeWar = "snipe-war";

    public static readonly IReadOnlyList<(string Name, string Description)> All =
    [
        (Steady, "Bots bid at a fixed rate on the chosen Live auctions for the duration."),
        (ClosingSpike, "Makes auctions that end together; light bidding until the last 60 s, then full rate."),
        (SnipeWar, "Makes a few auctions; bots bid mostly inside the snipe window, so extensions run to the cap."),
    ];

    public static bool IsKnown(string name) => All.Any(s => s.Name == name);

    /// <summary>True when the scenario makes its own demo auctions.</summary>
    public static bool MakesAuctions(string name) => name is ClosingSpike or SnipeWar;

    /// <summary>
    /// Bids per second for one auction now. steady: constant. closing-spike: 10 % until 60 s before the end,
    /// then 100 %. snipe-war: 5 % until the snipe window, then 100 % (also through extensions, as the end moves).
    /// </summary>
    public static double RateFor(string scenario, double fullRate, long nowMs, long endMs, int snipeWindowSec)
    {
        if (endMs > 0 && nowMs >= endMs)
        {
            return 0;
        }

        var left = endMs - nowMs;
        return scenario switch
        {
            ClosingSpike => left <= 60_000 ? fullRate : fullRate * 0.10,
            SnipeWar => left <= Math.Max(1, snipeWindowSec) * 1000L ? fullRate : fullRate * 0.05,
            _ => fullRate,
        };
    }
}

/// <summary>A token bucket per auction: the scheduler adds rate x time and fires one bid per whole token.</summary>
public sealed class TokenBucket
{
    private double _tokens;

    /// <summary>Adds rate x elapsed and returns how many bids to send now (at most burst).</summary>
    public int Take(double ratePerSec, TimeSpan elapsed, int burst = 50)
    {
        _tokens = Math.Min(burst, _tokens + (ratePerSec * elapsed.TotalSeconds));
        var whole = (int)Math.Floor(_tokens);
        _tokens -= whole;
        return whole;
    }
}

/// <summary>How a bot picks its max (R2.4): the minimum plus 0 to aggressiveness x 5 increments, under its own ceiling.</summary>
public static class BidChooser
{
    /// <summary>The max to bid in cents, or null when the bot should not bid (it leads, or the price is past its ceiling).</summary>
    public static long? Choose(long minNextC, long priceC, IncrementTable table, int aggressiveness, long ceilingC, bool botLeads, Random random)
    {
        if (botLeads || minNextC <= 0)
        {
            return null;
        }

        var steps = random.Next(0, (Math.Clamp(aggressiveness, 1, 5) * 5) + 1);
        var amount = minNextC + (steps * table.IncrementFor(Math.Max(priceC, 0)));
        return amount > ceilingC ? null : amount;
    }

    /// <summary>Each bot's top price for one auction: starting price x U(3, 20), fixed for the run.</summary>
    public static long Ceiling(long startC, Random random) => (long)(Math.Max(startC, 100) * (3 + (random.NextDouble() * 17)));
}

/// <summary>Bid latencies kept in a ring (the last 20 000), enough for P50 and P99 (design 1).</summary>
public sealed class LatencyReservoir
{
    private readonly double[] _values = new double[20_000];
    private readonly object _gate = new();
    private int _count;
    private int _next;

    public void Add(double ms)
    {
        lock (_gate)
        {
            _values[_next] = ms;
            _next = (_next + 1) % _values.Length;
            _count = Math.Min(_count + 1, _values.Length);
        }
    }

    /// <summary>The p-th percentile (0..100), or 0 with no samples.</summary>
    public double Percentile(double p)
    {
        double[] copy;
        lock (_gate)
        {
            if (_count == 0)
            {
                return 0;
            }

            copy = _values.Take(_count).ToArray();
        }

        Array.Sort(copy);
        var index = (int)Math.Ceiling(p / 100.0 * copy.Length) - 1;
        return Math.Round(copy[Math.Clamp(index, 0, copy.Length - 1)], 1);
    }
}
