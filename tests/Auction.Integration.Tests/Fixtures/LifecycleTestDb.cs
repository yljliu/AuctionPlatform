using Auction.Domain.Enums;
using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Integration.Tests.Fixtures;

/// <summary>
/// A fresh database for spec 05 tests: migrated, with a category, increment table 1, fee schedule 1
/// (buyer premium 5 %, seller fee 8 %) and the lifecycle lease held by "test".
/// </summary>
public sealed class LifecycleTestDb : IAsyncDisposable
{
    public static readonly Guid Seller = Guid.Parse("55555555-0000-0000-0000-000000000505");

    private readonly SqlServerFixture _sql;
    private int _categoryId;

    private LifecycleTestDb(SqlServerFixture sql, string name, AuctionDbContext db)
    {
        _sql = sql;
        Name = name;
        Db = db;
        Connections = new SqlConnectionFactory(sql.ConnectionStringFor(name));
    }

    public string Name { get; }

    public AuctionDbContext Db { get; }

    public SqlConnectionFactory Connections { get; }

    public LeaseManager Lease { get; private set; } = null!;

    public LeaseGrant Grant => Lease.Current!;

    public static async Task<LifecycleTestDb> CreateAsync(SqlServerFixture sql, string name)
    {
        var db = await sql.CreateEmptyContextAsync(name);
        await db.Database.MigrateAsync();
        var test = new LifecycleTestDb(sql, name, db);

        var category = new Category { Name = "c", Slug = "c" };
        db.Categories.Add(category);
        foreach (var (from, inc) in new[] { (0m, 1m), (50m, 2.5m), (250m, 5m) })
        {
            db.BidIncrementTiers.Add(new BidIncrementTier { TableId = 1, FromPrice = from, Increment = inc });
        }

        db.FeeSchedules.Add(new FeeSchedule
        {
            FeeScheduleId = 1, BuyerPremiumPct = 5m, SellerFinalValuePct = 8m, ListingFee = 0m, EffectiveFrom = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        test._categoryId = category.CategoryId;

        test.Lease = new LeaseManager(new SqlLeaseStore(test.Connections), new LeaseOptions { Holder = "test" },
            TimeProvider.System, NullLogger<LeaseManager>.Instance);
        (await test.Lease.RenewOnceAsync()).Should().BeTrue();
        return test;
    }

    /// <summary>A new context on the same database (to read what other code wrote).</summary>
    public AuctionDbContext NewContext() => _sql.CreateContext(Name);

    public sealed record AuctionSetup(
        AuctionStatus Status = AuctionStatus.Live,
        decimal StartingPrice = 10m,
        decimal? CurrentPrice = null,
        Guid? Leader = null,
        int BidCount = 0,
        decimal? Reserve = null,
        ShippingType Shipping = ShippingType.FreeShipping,
        decimal? ShippingCost = null,
        long StartsInMs = -3_600_000,
        long EndsInMs = 3_600_000,
        long LastSeq = 0,
        long LastESeq = 0);

    public async Task<AuctionEntity> AddAuctionAsync(AuctionSetup s)
    {
        var now = DateTime.UtcNow;
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = Seller, CategoryId = _categoryId, Title = "t", Description = "d",
            ShippingType = s.Shipping, ShippingCost = s.ShippingCost, Status = ItemStatus.InAuction, CreatedAt = now, UpdatedAt = now,
        };
        var auction = new AuctionEntity
        {
            AuctionId = Guid.NewGuid(), ItemId = item.ItemId, SellerId = Seller,
            StartTime = now.AddMilliseconds(s.StartsInMs), ScheduledEndTime = now.AddMilliseconds(s.EndsInMs), EndTime = now.AddMilliseconds(s.EndsInMs),
            StartingPrice = s.StartingPrice, ReservePrice = s.Reserve, FeeScheduleId = 1, IncrementTableId = 1,
            SnipeWindowSec = 0, ExtensionSec = 0, MaxTotalExtensionSec = 0,
            Status = s.Status, CurrentPrice = s.CurrentPrice, CurrentLeaderId = s.Leader, BidCount = s.BidCount,
            LastSeq = s.LastSeq, LastESeq = s.LastESeq, CreatedAt = now, UpdatedAt = now,
        };
        Db.Items.Add(item);
        Db.Auctions.Add(auction);
        await Db.SaveChangesAsync();
        return auction;
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}
