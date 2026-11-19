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

    private AuctionManager Manager(AuctionDbContext db, ILiveAuctionControl? live = null) =>
        new(db, new SellerGuard(db, TestServices.StatusCache(db, redis.Connection)), new OutboxWriter(db, TimeProvider.System),
            live ?? new StubLiveAuctionControl(), TimeProvider.System);

    /// <summary>Stands in for the Redis cancel script. Remembers calls and returns the answer you give it.</summary>
    private sealed class FakeLive(bool answer) : ILiveAuctionControl
    {
        public List<Guid> Calls { get; } = [];

        public Task<LiveControlOutcome> CancelAsync(Guid auctionId, bool force, CancellationToken ct = default)
        {
            Calls.Add(auctionId);
            return Task.FromResult(new LiveControlOutcome(answer ? LiveControlResult.Done : LiveControlResult.InvalidState));
        }

        public Task<LiveControlOutcome> CloseAsync(Guid auctionId, CancellationToken ct = default) => NotInRedis;

        public Task<LiveControlOutcome> FreezeAsync(Guid auctionId, CancellationToken ct = default) => NotInRedis;

        public Task<LiveControlOutcome> ResumeAsync(Guid auctionId, CancellationToken ct = default) => NotInRedis;

        private static Task<LiveControlOutcome> NotInRedis => Task.FromResult(new LiveControlOutcome(LiveControlResult.NotInRedis));
    }

    /// <summary>Sets status and bid count directly, as the lifecycle and bidding engine will later.</summary>
    private static async Task<string> ForceStateAsync(AuctionDbContext db, Guid auctionId, AuctionStatus status, int bidCount)
    {
        await db.Auctions.Where(a => a.AuctionId == auctionId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, status).SetProperty(a => a.BidCount, bidCount));
        db.ChangeTracker.Clear();
        return Convert.ToBase64String((await db.Auctions.AsNoTracking().SingleAsync(a => a.AuctionId == auctionId)).RowVersion);
    }

    // ---------- task 6: update ----------

    [Fact]
    public async Task Scheduled_auction_can_be_edited_with_the_right_row_version()
    {
        var (db, sellerId, itemId) = await SetUpAsync("update_ok");
        await using var _ = db;
        var created = await Manager(db).CreateAsync(sellerId, itemId, Input());
        db.ChangeTracker.Clear();

        var updated = await Manager(db).UpdateAsync(sellerId, created.Auction.AuctionId, Convert.ToBase64String(created.Auction.RowVersion),
            Input() with { StartingPrice = 40m, ReservePrice = null });

        updated.Auction.StartingPrice.Should().Be(40m);
        updated.Auction.ReservePrice.Should().BeNull();
        (await db.Outbox.CountAsync(o => o.Topic == OutboxTopics.AuctionUpdated)).Should().Be(1);
    }

    [Fact]
    public async Task Edit_with_an_old_row_version_is_VERSION_CONFLICT()
    {
        var (db, sellerId, itemId) = await SetUpAsync("update_stale");
        await using var _ = db;
        var created = await Manager(db).CreateAsync(sellerId, itemId, Input());
        var oldVersion = Convert.ToBase64String(created.Auction.RowVersion);
        db.ChangeTracker.Clear();
        await Manager(db).UpdateAsync(sellerId, created.Auction.AuctionId, oldVersion, Input() with { StartingPrice = 30m, ReservePrice = null });
        db.ChangeTracker.Clear();

        await FailsWith(() => Manager(db).UpdateAsync(sellerId, created.Auction.AuctionId, oldVersion, Input() with { StartingPrice = 35m, ReservePrice = null }),
            ErrorCodes.VersionConflict);
    }

    [Fact]
    public async Task Edit_after_a_bid_is_EDIT_LOCKED()
    {
        var (db, sellerId, itemId) = await SetUpAsync("update_after_bid");
        await using var _ = db;
        var created = await Manager(db).CreateAsync(sellerId, itemId, Input());
        var version = await ForceStateAsync(db, created.Auction.AuctionId, AuctionStatus.Live, bidCount: 1);

        await FailsWith(() => Manager(db).UpdateAsync(sellerId, created.Auction.AuctionId, version, Input()), ErrorCodes.EditLocked);
    }

    [Fact]
    public async Task Edit_of_a_live_auction_without_bids_is_INVALID_STATE()
    {
        var (db, sellerId, itemId) = await SetUpAsync("update_live");
        await using var _ = db;
        var created = await Manager(db).CreateAsync(sellerId, itemId, Input());
        var version = await ForceStateAsync(db, created.Auction.AuctionId, AuctionStatus.Live, bidCount: 0);

        await FailsWith(() => Manager(db).UpdateAsync(sellerId, created.Auction.AuctionId, version, Input()), ErrorCodes.InvalidState);
    }

    // ---------- task 6: cancel ----------

    [Fact]
    public async Task Cancel_scheduled_sets_cancelled_item_draft_and_outbox_and_asks_redis()
    {
        var (db, sellerId, itemId) = await SetUpAsync("cancel_scheduled");
        await using var _ = db;
        var created = await Manager(db).CreateAsync(sellerId, itemId, Input());
        db.ChangeTracker.Clear();
        var live = new FakeLive(true);

        var cancelled = await Manager(db, live).CancelAsync(sellerId, created.Auction.AuctionId, "Changed my mind");

        cancelled.Auction.Status.Should().Be(AuctionStatus.Cancelled);
        cancelled.Auction.CancelledReason.Should().Be("Changed my mind");
        cancelled.Item.Status.Should().Be(ItemStatus.Draft);
        live.Calls.Should().Equal([created.Auction.AuctionId], "a Scheduled auction may already be loaded into Redis");
        (await db.Outbox.CountAsync(o => o.Topic == OutboxTopics.AuctionCancelled)).Should().Be(1);
    }

    [Fact]
    public async Task Cancelled_item_can_be_listed_again()
    {
        var (db, sellerId, itemId) = await SetUpAsync("cancel_relist");
        await using var _ = db;
        var created = await Manager(db).CreateAsync(sellerId, itemId, Input());
        db.ChangeTracker.Clear();
        await Manager(db).CancelAsync(sellerId, created.Auction.AuctionId, "Wrong price");
        db.ChangeTracker.Clear();

        var again = await Manager(db).CreateAsync(sellerId, itemId, Input());

        again.Auction.Status.Should().Be(AuctionStatus.Scheduled);
    }

    [Fact]
    public async Task Cancel_live_without_bids_asks_the_live_copy_first()
    {
        var (db, sellerId, itemId) = await SetUpAsync("cancel_live");
        await using var _ = db;
        var created = await Manager(db).CreateAsync(sellerId, itemId, Input());
        await ForceStateAsync(db, created.Auction.AuctionId, AuctionStatus.Live, bidCount: 0);
        var live = new FakeLive(true);

        var cancelled = await Manager(db, live).CancelAsync(sellerId, created.Auction.AuctionId, "Item broke");

        cancelled.Auction.Status.Should().Be(AuctionStatus.Cancelled);
        live.Calls.Should().Equal(created.Auction.AuctionId);
    }

    [Fact]
    public async Task Cancel_live_loses_to_a_bid_that_arrives_at_the_same_moment()
    {
        var (db, sellerId, itemId) = await SetUpAsync("cancel_live_race");
        await using var _ = db;
        var created = await Manager(db).CreateAsync(sellerId, itemId, Input());
        await ForceStateAsync(db, created.Auction.AuctionId, AuctionStatus.Live, bidCount: 0);

        await FailsWith(() => Manager(db, new FakeLive(false)).CancelAsync(sellerId, created.Auction.AuctionId, "Item broke"), ErrorCodes.InvalidState);

        db.ChangeTracker.Clear();
        (await db.Auctions.SingleAsync()).Status.Should().Be(AuctionStatus.Live);
    }

    [Fact]
    public async Task Cancel_with_bids_is_INVALID_STATE()
    {
        var (db, sellerId, itemId) = await SetUpAsync("cancel_with_bids");
        await using var _ = db;
        var created = await Manager(db).CreateAsync(sellerId, itemId, Input());
        await ForceStateAsync(db, created.Auction.AuctionId, AuctionStatus.Live, bidCount: 2);

        await FailsWith(() => Manager(db).CancelAsync(sellerId, created.Auction.AuctionId, "Too late"), ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Cancel_after_the_end_is_INVALID_STATE()
    {
        var (db, sellerId, itemId) = await SetUpAsync("cancel_ended");
        await using var _ = db;
        var created = await Manager(db).CreateAsync(sellerId, itemId, Input());
        await ForceStateAsync(db, created.Auction.AuctionId, AuctionStatus.Settled, bidCount: 0);

        await FailsWith(() => Manager(db).CancelAsync(sellerId, created.Auction.AuctionId, "Too late"), ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Cancel_needs_a_reason()
    {
        var (db, sellerId, itemId) = await SetUpAsync("cancel_reason");
        await using var _ = db;
        var created = await Manager(db).CreateAsync(sellerId, itemId, Input());

        await FailsWith(() => Manager(db).CancelAsync(sellerId, created.Auction.AuctionId, "  "), ErrorCodes.ValidationFailed);
    }

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
