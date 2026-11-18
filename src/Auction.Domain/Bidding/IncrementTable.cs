namespace Auction.Domain.Bidding;

/// <summary>
/// Bid increment tiers in cents (spec 03 design 3). Example table 1:
/// from 0 add 1.00, from 50 add 2.50, from 250 add 5.00, from 1000 add 10.00, from 5000 add 50.00.
/// </summary>
public sealed record IncrementTable(IReadOnlyList<(long FromC, long IncC)> Tiers)
{
    /// <summary>The increment of the last tier whose start is at or below the price.</summary>
    public long IncrementFor(long priceC)
    {
        if (Tiers.Count == 0)
        {
            throw new InvalidOperationException("Increment table has no tiers.");
        }

        var increment = Tiers[0].IncC;
        foreach (var (fromC, incC) in Tiers)
        {
            if (fromC <= priceC)
            {
                increment = incC;
            }
            else
            {
                break;
            }
        }

        return increment;
    }

    /// <summary>The smallest bid that is accepted: the starting price if no bids yet, else price + increment.</summary>
    public long MinNext(long priceC, int bidCount, long startC) =>
        bidCount == 0 ? startC : priceC + IncrementFor(priceC);

    /// <summary>Builds a table from database rows in money units (sorted by start).</summary>
    public static IncrementTable FromMoney(IEnumerable<(decimal From, decimal Increment)> tiers) =>
        new(tiers.OrderBy(t => t.From).Select(t => (ToCents(t.From), ToCents(t.Increment))).ToList());

    public static long ToCents(decimal amount) => (long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
}