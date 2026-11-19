using Auction.Infrastructure.Lifecycle;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 05 task 3 without Redis or SQL: turning a SQL row into load.lua values, and the loop settings.</summary>
public class LifecycleLoopUnitTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

    private static ScheduledAuctionRow Row(decimal? reserve = null) => new(
        Guid.NewGuid(), Guid.NewGuid(), "USD", Start, Start.AddHours(2), StartingPrice: 12.50m, reserve,
        IncrementTableId: 1, SnipeWindowSec: 60, ExtensionSec: 30, MaxTotalExtensionSec: 3600);

    [Fact]
    public void Times_become_unix_milliseconds()
    {
        var load = Row().ToLoad("0:100");

        load.StartMs.Should().Be(new DateTimeOffset(Start).ToUnixTimeMilliseconds());
        (load.EndMs - load.StartMs).Should().Be(2 * 3600 * 1000);
    }

    [Fact]
    public void Times_read_from_sql_without_a_kind_are_taken_as_utc()
    {
        var row = Row() with { StartTime = DateTime.SpecifyKind(Start, DateTimeKind.Unspecified) };

        row.ToLoad("0:100").StartMs.Should().Be(new DateTimeOffset(Start).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Prices_become_cents_and_seconds_become_milliseconds()
    {
        var load = Row(reserve: 99.99m).ToLoad("0:100,5000:250");

        load.StartC.Should().Be(1250);
        load.ReserveC.Should().Be(9999);
        load.SnipeMs.Should().Be(60_000);
        load.ExtMs.Should().Be(30_000);
        load.MaxExtMs.Should().Be(3_600_000);
        load.IncrementTable.Should().Be("0:100,5000:250");
        load.Currency.Should().Be("USD");
    }

    [Fact]
    public void No_reserve_stays_empty()
    {
        Row().ToLoad("0:100").ReserveC.Should().BeNull();
    }

    [Fact]
    public void Default_settings_match_the_design()
    {
        var options = new LifecycleOptions();

        options.TickEvery.Should().Be(TimeSpan.FromMilliseconds(250));
        options.LoadAhead.Should().Be(TimeSpan.FromMinutes(5));
        options.BatchSize.Should().Be(100);
        options.OnlyPartitions.Should().BeNull();
    }
}
