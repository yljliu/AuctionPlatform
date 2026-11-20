namespace Auction.Infrastructure.Persistence;

/// <summary>Cuts a time down to whole milliseconds, the precision Redis and the live clock use.</summary>
public static class TimeCut
{
    public static DateTime ToMs(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Kind);
}
