using Auction.Domain.Errors;

namespace Auction.Domain.Bidding;

/// <summary>
/// Checks the max bid a user typed (R3.4) and turns it into whole cents.
/// The amount arrives like the Money message: whole units plus nanos (billionths).
/// </summary>
public static class BidAmountRules
{
    private const int NanosPerCent = 10_000_000;

    /// <summary>The biggest bid allowed: 10 billion. Far below where Lua numbers lose cents.</summary>
    public const long MaxUnits = 10_000_000_000;

    public static long ToCents(long units, int nanos, string? currency)
    {
        var issues = new List<FieldIssue>();

        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
        {
            issues.Add(new FieldIssue("max_amount.currency", "format", "Currency must be a 3 letter code like USD."));
        }

        // units and nanos must have the same sign, and nanos must be under one unit.
        var signsDisagree = (units > 0 && nanos < 0) || (units < 0 && nanos > 0);
        if (nanos <= -1_000_000_000 || nanos >= 1_000_000_000 || signsDisagree)
        {
            issues.Add(new FieldIssue("max_amount", "format", "The amount is not a valid money value."));
        }
        else if (units < 0 || nanos < 0 || (units == 0 && nanos == 0))
        {
            issues.Add(new FieldIssue("max_amount", "min", "Your max bid must be more than 0."));
        }
        else if (nanos % NanosPerCent != 0)
        {
            issues.Add(new FieldIssue("max_amount", "decimals", "Use at most 2 decimal places."));
        }
        else if (units > MaxUnits)
        {
            issues.Add(new FieldIssue("max_amount", "max", "That bid is too large."));
        }

        if (issues.Count > 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The bid amount is not valid.", issues);
        }

        return (units * 100) + (nanos / NanosPerCent);
    }

    /// <summary>
    /// What makes two bids "the same" for idempotency (R6.2): who, how much, which currency.
    /// The same key with a different fingerprint is IDEMPOTENCY_MISMATCH.
    /// </summary>
    public static string Fingerprint(Guid userId, long maxC, string currency) =>
        $"{userId}:{maxC}:{currency.Trim().ToUpperInvariant()}";
}
