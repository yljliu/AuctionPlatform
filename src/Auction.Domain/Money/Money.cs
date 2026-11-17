namespace Auction.Domain;

/// <summary>An amount of money in one currency. Always rounded to 4 decimals (matches DECIMAL(19,4)).</summary>
public readonly record struct Money
{
    public const int Decimals = 4;

    public decimal Amount { get; }
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3 letter code.", nameof(currency));

        Amount = Round(amount);
        Currency = currency.ToUpperInvariant();
    }

    /// <summary>Round to 4 decimals. Halves go away from zero (0.00005 becomes 0.0001).</summary>
    public static decimal Round(decimal amount) =>
        Math.Round(amount, Decimals, MidpointRounding.AwayFromZero);

    /// <summary>Whole cents (used in Redis and Lua). Halves go away from zero.</summary>
    public long ToCents() =>
        (long)Math.Round(Amount * 100m, 0, MidpointRounding.AwayFromZero);

    public static Money FromCents(long cents, string currency) =>
        new(cents / 100m, currency);

    public static Money operator +(Money a, Money b)
    {
        SameCurrency(a, b);
        return new Money(a.Amount + b.Amount, a.Currency);
    }

    public static Money operator -(Money a, Money b)
    {
        SameCurrency(a, b);
        return new Money(a.Amount - b.Amount, a.Currency);
    }

    private static void SameCurrency(Money a, Money b)
    {
        if (a.Currency != b.Currency)
            throw new InvalidOperationException($"Currency mismatch: {a.Currency} and {b.Currency}.");
    }

    public override string ToString() => $"{Amount:0.00##} {Currency}";
}