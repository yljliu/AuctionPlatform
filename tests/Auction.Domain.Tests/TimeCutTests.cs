using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Persistence;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>
/// Verifier bug: SQL kept auction times to the microsecond, Redis to the millisecond. The persister's end time (from
/// Redis) came out a fraction below ScheduledEndTime and SQL's CK_Times refused the bid, so it was dead-lettered.
/// </summary>
public class TimeCutTests
{
    [Fact]
    public void Times_are_cut_to_whole_milliseconds_and_keep_their_kind()
    {
        var fine = new DateTime(2026, 11, 20, 8, 38, 34, DateTimeKind.Utc).AddTicks(8149410);   // .8149410 s

        var cut = TimeCut.ToMs(fine);

        cut.Should().Be(new DateTime(2026, 11, 20, 8, 38, 34, 814, DateTimeKind.Utc));
        cut.Kind.Should().Be(DateTimeKind.Utc);
        TimeCut.ToMs(cut).Should().Be(cut, "already whole");
    }

    [Fact]
    public void A_cut_time_round_trips_through_redis_milliseconds_unchanged()
    {
        var scheduledEnd = TimeCut.ToMs(DateTime.UtcNow.AddTicks(1234));
        var redisEndMs = new DateTimeOffset(scheduledEnd).ToUnixTimeMilliseconds();

        var persisted = DateTimeOffset.FromUnixTimeMilliseconds(redisEndMs).UtcDateTime;

        persisted.Should().BeOnOrAfter(scheduledEnd, "CK_Times needs EndTime >= ScheduledEndTime");
    }
}
