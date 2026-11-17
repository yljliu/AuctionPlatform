using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Auction.Integration.Tests;

[Collection(SqlServerCollection.Name)]
public class AuctionManagerTests(SqlServerFixture sql, RedisFixture redis)
{
    /// <summary>Fresh database: one seller, one Draft item, increment table 1, and an image unless told not to.</summary>
    private async Task<(AuctionDbContext Db, Guid SellerId, Guid ItemId)> SetUpAsync(string name, bool withImage = true)
    {
        var db = await sql.CreateEmptyContextAsync("auctions_" + name);
        await db.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        var seller = new User
        {
            UserId = Guid.NewGuid(), Username = "seller1", Email = "seller1@test.local", PasswordHash = "x",
            Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        seller.Roles.Add(new UserRole { UserId = seller.UserId, Role = Role.Seller, GrantedAt = now });
        var category = new Category { Name = "Watches", Slug = "watches" };
        db.Users.Add(seller);
        db.Categories.Add(category);
        db.BidIncrementTiers.Add(new BidIncrementTier { TableId = 1, FromPrice = 0, Increment = 1 });
        await db.SaveChangesAsync();

        db.SellerProfiles.Add(new SellerProfile { UserId = seller.UserId, DisplayName = "Shop", Status = SellerStatus.Verified });
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = seller.UserId, CategoryId = category.CategoryId, Title = "Seiko", Description = "d",
            ShippingType = ShippingType.FreeShipping, Status = ItemStatus.Draft, CreatedAt = now, UpdatedAt = now,
        };
        db.Items.Add(item);
        if (withImage)
        {
            db.ItemImages.Add(new ItemImage
            {
                ImageId = Guid.NewGuid(), ItemId = item.ItemId, StorageKey = "x/y.png", ContentType = "image/png",
                Bytes = 10, Status = ImageStatus.Ready, CreatedAt = now,
            });
        }

        await db.SaveChangesAsync();
        return (db, seller.UserId, item.ItemId);
    }

    private AuctionManager Manager(AuctionDbContext db) =>
        new(db, new SellerGuard(db, TestServices.StatusCache(db, redis.Connection)), new OutboxWriter(db, TimeProvider.System), TimeProvider.System);

    private static AuctionInput Input() => new(DateTime.UtcNow.AddMinutes(10), DateTime.UtcNow.AddMinutes(70), 25m, 100m);

    private static async Task FailsWith(Func<Task> act, string code) =>
        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(code);

    [Fact]
    public async Task Create_saves_a_scheduled_auction_publishes_the_item_and_writes_the_outbox()
    {
        var (db, sellerId, itemId) = await SetUpAsync("create");
        await using var _ = db;

        var view = await Manager(db).CreateAsync(sellerId, itemId, Input());

        view.Auction.Status.Should().Be(AuctionStatus.Scheduled);
        view.Auction.EndTime.Should().Be(view.Auction.ScheduledEndTime);
        view.Auction.SnipeWindowSec.Should().Be(60);
        view.Auction.Currency.Should().Be("USD");
        view.Auction.ReservePrice.Should().Be(100m);

        db.ChangeTracker.Clear();
        (await db.Items.SingleAsync()).Status.Should().Be(ItemStatus.Published);

        var outbox = await db.Outbox.SingleAsync();
        outbox.Topic.Should().Be(OutboxTopics.AuctionCreated);
        outbox.Key.Should().Be(view.Auction.AuctionId.ToString());
        outbox.Payload.Should().Contain(itemId.ToString());
        outbox.PublishedAt.Should().BeNull();
    }

    [Fact]
    public async Task Second_auction_for_a_published_item_is_INVALID_STATE()
    {
        var (db, sellerId, itemId) = await SetUpAsync("second");
        await using var _ = db;
        await Manager(db).CreateAsync(sellerId, itemId, Input());

        await FailsWith(() => Manager(db).CreateAsync(sellerId, itemId, Input()), ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Unique_index_stops_a_second_open_auction_even_if_the_item_check_is_passed()
    {
        var (db, sellerId, itemId) = await SetUpAsync("race");
        await using var _ = db;
        await Manager(db).CreateAsync(sellerId, itemId, Input());

        // Pretend a racing request read the item while it was still Draft.
        await db.Items.ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ItemStatus.Draft));
        db.ChangeTracker.Clear();

        await FailsWith(() => Manager(db).CreateAsync(sellerId, itemId, Input()), ErrorCodes.InvalidState);

        db.ChangeTracker.Clear();
        (await db.Auctions.CountAsync()).Should().Be(1);
        (await db.Outbox.CountAsync()).Should().Be(1, "the failed create left no outbox row");
    }

    [Fact]
    public async Task No_image_is_IMAGE_REQUIRED()
    {
        var (db, sellerId, itemId) = await SetUpAsync("no_image", withImage: false);
        await using var _ = db;

        await FailsWith(() => Manager(db).CreateAsync(sellerId, itemId, Input()), ErrorCodes.ImageRequired);
    }

    [Fact]
    public async Task Bad_times_are_VALIDATION_FAILED()
    {
        var (db, sellerId, itemId) = await SetUpAsync("bad_times");
        await using var _ = db;

        await FailsWith(() => Manager(db).CreateAsync(sellerId, itemId, Input() with { StartUtc = DateTime.UtcNow }), ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Another_sellers_item_is_NOT_OWNER()
    {
        var (db, _, itemId) = await SetUpAsync("not_owner");
        await using var _db = db;
        var now = DateTime.UtcNow;
        var other = new User
        {
            UserId = Guid.NewGuid(), Username = "seller2", Email = "seller2@test.local", PasswordHash = "x",
            Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        other.Roles.Add(new UserRole { UserId = other.UserId, Role = Role.Seller, GrantedAt = now });
        db.Users.Add(other);
        await db.SaveChangesAsync();
        db.SellerProfiles.Add(new SellerProfile { UserId = other.UserId, DisplayName = "Other", Status = SellerStatus.Verified });
        await db.SaveChangesAsync();

        await FailsWith(() => Manager(db).CreateAsync(other.UserId, itemId, Input()), ErrorCodes.NotOwner);
    }
}
