
namespace Auction.Contracts;

public static class MoneyConversions
{
    private const decimal NanoFactor = 1_000_000_000m;

    public static Money ToProto(this Domain.Money money)
    {
        var units = decimal.Truncate(money.Amount);
        var nanos = (int)((money.Amount - units) * NanoFactor);
        return new Money { Currency = money.Currency, Units = (long)units, Nanos = nanos };
    }

    public static Domain.Money ToDomain(this Money proto) =>
        new(proto.Units + proto.Nanos / NanoFactor, proto.Currency);
}
