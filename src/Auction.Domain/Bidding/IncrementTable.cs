using System.Globalization;
using System.Text;

namespace Auction.Domain.Bidding;

/// <summary>
/// Bid increment tiers in cents (spec 03 design 3, R3). Example table 1:
/// from 0 add 1.00, from 50 add 2.50, from 250 add 5.00, from 1000 add 10.00, from 5000 add 50.00.
/// A table always starts at 0, starts go strictly up, and every increment is more than 0.
/// </summary>
public sealed record IncrementTable(IReadOnlyList<(long FromC, long IncC)> Tiers)
{
    public IReadOnlyList<(long FromC, long IncC)> Tiers { get; } = Check(Tiers);

    /// <summary>The increment of the last tier whose start is at or below the price.</summary>
    public long IncrementFor(long priceC)
    {
        var increment = Tiers[0].IncC;
        foreach (var (fromC, incC) in Tiers)
        {
            if (fromC > priceC)
            {
                break;
            }

            increment = incC;
        }

        return increment;
    }

    /// <summary>The smallest bid that is accepted: the starting price if no bids yet, else price + increment (R3.1, R3.2).</summary>
    public long MinNext(long priceC, int bidCount, long startC) =>
        bidCount == 0 ? startC : priceC + IncrementFor(priceC);

    /// <summary>
    /// The form kept in the Redis state hash field "inc" and read by bid.lua:
    /// "0:100,5000:250,25000:500,100000:1000,500000:5000".
    /// </summary>
    public string ToRedisString()
    {
        var text = new StringBuilder();
        foreach (var (fromC, incC) in Tiers)
        {
            if (text.Length > 0)
            {
                text.Append(',');
            }

            text.Append(fromC.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(incC.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>Reads the "inc" string back. Throws FormatException if it is not in that form.</summary>
    public static IncrementTable Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("Increment string is empty.");
        }

        var tiers = new List<(long, long)>();
        foreach (var part in text.Split(','))
        {
            var pieces = part.Split(':');
            if (pieces.Length != 2
                || !long.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var fromC)
                || !long.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var incC))
            {
                throw new FormatException($"Bad increment tier '{part}'. Expected from:increment in cents.");
            }

            tiers.Add((fromC, incC));
        }

        try
        {
            return new IncrementTable(tiers);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Builds a table from database rows in money units (catalog.BidIncrementTiers). Sorts by start.
    /// Every value must be whole cents.
    /// </summary>
    public static IncrementTable FromMoney(IEnumerable<(decimal From, decimal Increment)> tiers)
    {
        var cents = new List<(long, long)>();
        foreach (var (from, increment) in tiers.OrderBy(t => t.From))
        {
            cents.Add((WholeCents(from, "start"), WholeCents(increment, "increment")));
        }

        return new IncrementTable(cents);
    }

    /// <summary>Money to cents, halves away from zero. For prices that may have more decimals.</summary>
    public static long ToCents(decimal amount) => (long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    private static long WholeCents(decimal amount, string what)
    {
        var cents = amount * 100m;
        if (cents != decimal.Truncate(cents))
        {
            throw new ArgumentException($"Increment tier {what} {amount} is not a whole number of cents.");
        }

        return (long)cents;
    }

    private static IReadOnlyList<(long FromC, long IncC)> Check(IReadOnlyList<(long FromC, long IncC)>? tiers)
    {
        if (tiers == null || tiers.Count == 0)
        {
            throw new ArgumentException("An increment table needs at least one tier.");
        }

        if (tiers[0].FromC != 0)
        {
            throw new ArgumentException("The first increment tier must start at 0.");
        }

        for (var i = 0; i < tiers.Count; i++)
        {
            if (tiers[i].IncC <= 0)
            {
                throw new ArgumentException($"Increment tier {i} has an increment of {tiers[i].IncC}; it must be more than 0.");
            }

            if (i > 0 && tiers[i].FromC <= tiers[i - 1].FromC)
            {
                throw new ArgumentException("Increment tier starts must go strictly up.");
            }
        }

        return tiers.ToList();   // a private copy, so callers cannot change it later
    }
}
