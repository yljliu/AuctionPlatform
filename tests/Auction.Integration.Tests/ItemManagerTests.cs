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
public class ItemManagerTests(SqlServerFixture sql, RedisFixture redis)
{
    private static ItemInput GoodInput(int categoryId) => new(
        categoryId, "Seiko diver", "Boxed, runs well.", "Good", "None", null,
        "BuyerPays", 10m, 2, false, null, null);

    /// <summary>Fresh database with one category, one verified seller and one plain bidder.</summary>
    private async Task<(AuctionDbContext Db, int CategoryId, Guid SellerId, Guid BidderId)> SetUpAsync(string name)
    {
        var db = await sql.CreateEmptyContextAsync("items_" + name);
        await db.Database.MigrateAsync();

        var category = new Category { Name = "Watches", Slug = "watches" };
        db.Categories.Add(category);

        var now = DateTime.UtcNow;
        var seller = NewUser("seller1", now, Role.Bidder, Role.Seller);
        var bidder = NewUser("alice", now, Role.Bidder);
        db.Users.AddRange(seller, bidder);
        await db.SaveChangesAsync();

        db.SellerProfiles.Add(new SellerProfile { UserId = seller.UserId, DisplayName = "Shop", Status = SellerStatus.Verified, VerifiedAt = now });
        await db.SaveChangesAsync();

        return (db, category.CategoryId, seller.UserId, bidder.UserId);
    }

    private static User NewUser(string name, DateTime now, params Role[] roles)
    {
        var user = new User
        {
            UserId = Guid.NewGuid(), Username = name, Email = name + "@test.local", PasswordHash = "x",
            Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        foreach (var role in roles)
        {
            user.Roles.Add(new UserRole { UserId = user.UserId, Role = role, GrantedAt = now });
        }

        return user;
    }

    private ItemManager Manager(AuctionDbContext db) =>
        new(db, new SellerGuard(db, TestServices.StatusCache(db, redis.Connection)), TimeProvider.System);

    private static async Task<DomainException> FailsWith(Func<Task> act, string code)
    {
        var ex = (await act.Should().ThrowAsync<DomainException>()).Which;
        ex.Code.Should().Be(code);
        return ex;
    }

    [Fact]
    public async Task Seller_creates_a_draft_item()
    {
        var (db, categoryId, sellerId, _) = await SetUpAsync("create");
        await using var _db = db;

        var created = await Manager(db).CreateAsync(sellerId, GoodInput(categoryId));

        created.Item.Status.Should().Be(ItemStatus.Draft);
        created.Item.RowVersion.Should().NotBeEmpty();
        (await db.Items.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Non_seller_gets_ROLE_REQUIRED()
    {
        var (db, categoryId, _, bidderId) = await SetUpAsync("not_seller");
        await using var _db = db;

        await FailsWith(() => Manager(db).CreateAsync(bidderId, GoodInput(categoryId)), ErrorCodes.RoleRequired);
    }

    [Fact]
    public async Task Missing_shipping_cost_names_the_field()
    {
        var (db, categoryId, sellerId, _) = await SetUpAsync("no_shipping");
        await using var _db = db;

        var ex = await FailsWith(() => Manager(db).CreateAsync(sellerId, GoodInput(categoryId) with { ShippingCost = null }), ErrorCodes.ValidationFailed);

        ex.Fields.Should().ContainSingle().Which.Field.Should().Be("shipping_cost");
    }

    [Fact]
    public async Task Unknown_category_is_refused()
    {
        var (db, _, sellerId, _) = await SetUpAsync("bad_category");
        await using var _db = db;

        var ex = await FailsWith(() => Manager(db).CreateAsync(sellerId, GoodInput(9999)), ErrorCodes.ValidationFailed);

        ex.Fields.Single().Field.Should().Be("category_id");
    }

    [Fact]
    public async Task Update_with_the_right_row_version_saves()
    {
        var (db, categoryId, sellerId, _) = await SetUpAsync("update_ok");
        await using var _db = db;
        var manager = Manager(db);
        var created = await manager.CreateAsync(sellerId, GoodInput(categoryId));

        var updated = await manager.UpdateAsync(sellerId, created.Item.ItemId, Convert.ToBase64String(created.Item.RowVersion),
            GoodInput(categoryId) with { Title = "Seiko diver, new strap" });

        updated.Item.Title.Should().Be("Seiko diver, new strap");
    }

    [Fact]
    public async Task Update_with_an_old_row_version_gets_VERSION_CONFLICT()
    {
        var (db, categoryId, sellerId, _) = await SetUpAsync("update_stale");
        await using var _db = db;
        var manager = Manager(db);
        var created = await manager.CreateAsync(sellerId, GoodInput(categoryId));
        var oldVersion = Convert.ToBase64String(created.Item.RowVersion);
        await manager.UpdateAsync(sellerId, created.Item.ItemId, oldVersion, GoodInput(categoryId) with { Title = "First edit" });

        await FailsWith(() => manager.UpdateAsync(sellerId, created.Item.ItemId, oldVersion, GoodInput(categoryId) with { Title = "Second edit" }),
            ErrorCodes.VersionConflict);
    }

    [Fact]
    public async Task Locked_item_refuses_a_title_change()
    {
        var (db, categoryId, sellerId, _) = await SetUpAsync("locked");
        await using var _db = db;
        var manager = Manager(db);
        var created = await manager.CreateAsync(sellerId, GoodInput(categoryId));
        await db.Items.ExecuteUpdateAsync(s => s.SetProperty(i => i.LockedAt, DateTime.UtcNow));   // as if a bid came in
        db.ChangeTracker.Clear();
        var version = Convert.ToBase64String((await db.Items.AsNoTracking().SingleAsync()).RowVersion);

        await FailsWith(() => manager.UpdateAsync(sellerId, created.Item.ItemId, version, GoodInput(categoryId) with { Title = "Changed" }),
            ErrorCodes.EditLocked);
    }

    [Fact]
    public async Task Other_seller_gets_NOT_OWNER()
    {
        var (db, categoryId, sellerId, _) = await SetUpAsync("not_owner");
        await using var _db = db;
        var created = await Manager(db).CreateAsync(sellerId, GoodInput(categoryId));

        var other = NewUser("seller2", DateTime.UtcNow, Role.Seller);
        db.Users.Add(other);
        await db.SaveChangesAsync();
        db.SellerProfiles.Add(new SellerProfile { UserId = other.UserId, DisplayName = "Other", Status = SellerStatus.Verified });
        await db.SaveChangesAsync();

        await FailsWith(() => Manager(db).DeleteDraftAsync(other.UserId, created.Item.ItemId), ErrorCodes.NotOwner);
    }

    [Fact]
    public async Task Delete_draft_removes_it_and_list_shows_only_mine()
    {
        var (db, categoryId, sellerId, _) = await SetUpAsync("delete_list");
        await using var _db = db;
        var manager = Manager(db);
        var keep = await manager.CreateAsync(sellerId, GoodInput(categoryId) with { Title = "Keep" });
        var drop = await manager.CreateAsync(sellerId, GoodInput(categoryId) with { Title = "Drop" });

        await manager.DeleteDraftAsync(sellerId, drop.Item.ItemId);
        var page = await manager.ListMineAsync(sellerId, null, Paging.From(1, 20));

        page.Total.Should().Be(1);
        page.Items.Single().Item.ItemId.Should().Be(keep.Item.ItemId);
    }
}
