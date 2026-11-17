using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Auction.Integration.Tests;

[Collection(SqlServerCollection.Name)]
public class MigrationTests(SqlServerFixture sql)
{
    private static readonly string[] ExpectedTables =
    [
        "ident.Users", "ident.UserRoles", "ident.RefreshTokens", "ident.SellerProfiles",
        "catalog.Categories", "catalog.Items", "catalog.ItemImages", "catalog.Auctions",
        "catalog.BidIncrementTiers", "catalog.FeeSchedules",
        "bidding.Bids", "bidding.AuctionEvents", "bidding.LifecycleLeases", "bidding.Watchlist",
        "pay.Settlements",
        "notify.Outbox", "notify.Notifications",
        "ops.AuditLog", "ops.SimulationRuns",
    ];

    [Fact]
    public async Task Migrate_on_empty_database_creates_all_mvp_tables()
    {
        await using var db = await sql.CreateEmptyContextAsync("migrate_all_tables");

        await db.Database.MigrateAsync();

        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT TABLE_SCHEMA + '.' + TABLE_NAME AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'")
            .ToListAsync();

        tables.Should().Contain(ExpectedTables);
    }

    [Fact]
    public async Task Migrate_twice_does_nothing_the_second_time()
    {
        await using var db = await sql.CreateEmptyContextAsync("migrate_twice");

        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();

        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Money_columns_are_decimal_19_4_and_times_are_datetime2_6()
    {
        await using var db = await sql.CreateEmptyContextAsync("column_types");
        await db.Database.MigrateAsync();

        var rows = await db.Database
            .SqlQueryRaw<string>(@"SELECT DATA_TYPE + '(' + CAST(COALESCE(NUMERIC_PRECISION, DATETIME_PRECISION) AS varchar) + ',' + CAST(COALESCE(NUMERIC_SCALE, 0) AS varchar) + ')' AS [Value]
                                   FROM INFORMATION_SCHEMA.COLUMNS
                                   WHERE TABLE_SCHEMA = 'catalog' AND TABLE_NAME = 'Auctions' AND COLUMN_NAME IN ('StartingPrice','StartTime')")
            .ToListAsync();

        rows.Should().BeEquivalentTo("decimal(19,4)", "datetime2(6,0)");
    }

    [Fact]
    public async Task Second_open_auction_for_the_same_item_is_rejected_by_the_unique_index()
    {
        await using var db = await sql.CreateEmptyContextAsync("one_open_auction");
        await db.Database.MigrateAsync();
        var itemId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await db.Database.ExecuteSqlRawAsync("INSERT catalog.Categories (Name, Slug, SortOrder, IsActive) VALUES ('c','c',0,1)");
        await db.Database.ExecuteSqlAsync($@"INSERT catalog.Items (ItemId, SellerId, CategoryId, Title, Description, Condition, WarrantyType, ShippingType, HandlingDays, ReturnsAccepted, Status, CreatedAt, UpdatedAt)
            SELECT {itemId}, {Guid.NewGuid()}, CategoryId, 't', 'd', 0, 0, 0, 1, 0, 1, {now}, {now} FROM catalog.Categories");

        async Task InsertAuction() => await db.Database.ExecuteSqlAsync($@"INSERT catalog.Auctions
            (AuctionId, ItemId, SellerId, StartTime, ScheduledEndTime, EndTime, StartingPrice, FeeScheduleId, Status, CreatedAt, UpdatedAt)
            VALUES ({Guid.NewGuid()}, {itemId}, {Guid.NewGuid()}, {now.AddHours(1)}, {now.AddHours(2)}, {now.AddHours(2)}, 10, 1, 1, {now}, {now})");

        await InsertAuction();
        var second = async () => await InsertAuction();

        await second.Should().ThrowAsync<Exception>().WithMessage("*UX_OneOpenAuctionPerItem*");
    }
}