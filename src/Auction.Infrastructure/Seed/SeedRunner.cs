using System.Security.Cryptography;
using System.Text;
using Auction.Domain.Enums;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Infrastructure.Seed;

/// <summary>
/// Fills an empty database with demo data. Safe to run many times:
/// each row has a stable key (username, slug, id made from a name),
/// and a row that already exists is left alone.
/// </summary>
public class SeedRunner
{
    private const int BcryptCost = 12;

    private readonly AuctionDbContext _db;
    private readonly string _password;
    private readonly DateTime _now;

    public SeedRunner(AuctionDbContext db, string password, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
        {
            throw new ArgumentException("SEED_PASSWORD must be at least 12 characters.", nameof(password));
        }

        _db = db;
        _password = password;
        _now = nowUtc;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await SeedUsersAsync(ct);
        await SeedCategoriesAsync(ct);
        await SeedIncrementTableAsync(ct);
        await SeedFeeScheduleAsync(ct);
        await SeedAuctionsAsync(ct);
    }

    // ---------- users ----------

    private static readonly string[] Sellers = ["seller1", "seller2", "seller3"];
    private static readonly string[] Bidders = ["alice", "bob", "carol", "dave", "erin"];

    private async Task SeedUsersAsync(CancellationToken ct)
    {
        await AddUserIfMissingAsync("admin", [Role.Bidder, Role.Admin], ct);

        foreach (var name in Sellers)
        {
            var user = await AddUserIfMissingAsync(name, [Role.Bidder, Role.Seller], ct);

            var hasProfile = await _db.SellerProfiles.AnyAsync(p => p.UserId == user.UserId, ct);
            if (!hasProfile)
            {
                _db.SellerProfiles.Add(new SellerProfile
                {
                    UserId = user.UserId,
                    DisplayName = "Shop of " + name,
                    Status = SellerStatus.Verified,
                    VerifiedAt = _now,
                });
                await _db.SaveChangesAsync(ct);
            }
        }

        foreach (var name in Bidders)
        {
            await AddUserIfMissingAsync(name, [Role.Bidder], ct);
        }
    }

    private async Task<User> AddUserIfMissingAsync(string username, Role[] roles, CancellationToken ct)
    {
        var existing = await _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (existing != null)
        {
            return existing;
        }

        var user = new User
        {
            UserId = StableId("user:" + username),
            Username = username,
            Email = username + "@demo.local",
            EmailVerifiedAt = _now,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(_password, BcryptCost),
            Status = UserStatus.Active,
            CreatedAt = _now,
            UpdatedAt = _now,
        };

        foreach (var role in roles)
        {
            user.Roles.Add(new UserRole { UserId = user.UserId, Role = role, GrantedAt = _now });
        }

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    // ---------- categories ----------

    // parent name, parent slug, then children (name, slug)
    private static readonly (string Name, string Slug, (string Name, string Slug)[] Children)[] CategoryTree =
    [
        ("Watches", "watches", [("Vintage", "watches-vintage"), ("Modern", "watches-modern")]),
        ("Art", "art", [("Paintings", "art-paintings"), ("Prints", "art-prints")]),
        ("Collectibles", "collectibles", [("Coins", "collectibles-coins"), ("Cards", "collectibles-cards")]),
    ];

    private async Task SeedCategoriesAsync(CancellationToken ct)
    {
        var order = 0;
        foreach (var parent in CategoryTree)
        {
            var parentRow = await AddCategoryIfMissingAsync(parent.Name, parent.Slug, null, order, ct);
            order++;

            var childOrder = 0;
            foreach (var child in parent.Children)
            {
                await AddCategoryIfMissingAsync(child.Name, child.Slug, parentRow.CategoryId, childOrder, ct);
                childOrder++;
            }
        }
    }

    private async Task<Category> AddCategoryIfMissingAsync(string name, string slug, int? parentId, int sortOrder, CancellationToken ct)
    {
        var existing = await _db.Categories.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (existing != null)
        {
            return existing;
        }

        var row = new Category { Name = name, Slug = slug, ParentCategoryId = parentId, SortOrder = sortOrder, IsActive = true };
        _db.Categories.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    // ---------- increment table and fees ----------

    private static readonly (decimal From, decimal Increment)[] DefaultTiers =
    [
        (0m, 1.00m), (50m, 2.50m), (250m, 5.00m), (1000m, 10.00m), (5000m, 50.00m),
    ];

    private async Task SeedIncrementTableAsync(CancellationToken ct)
    {
        foreach (var tier in DefaultTiers)
        {
            var exists = await _db.BidIncrementTiers.AnyAsync(t => t.TableId == 1 && t.FromPrice == tier.From, ct);
            if (!exists)
            {
                _db.BidIncrementTiers.Add(new BidIncrementTier { TableId = 1, FromPrice = tier.From, Increment = tier.Increment });
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task SeedFeeScheduleAsync(CancellationToken ct)
    {
        var exists = await _db.FeeSchedules.AnyAsync(f => f.FeeScheduleId == 1, ct);
        if (!exists)
        {
            _db.FeeSchedules.Add(new FeeSchedule
            {
                FeeScheduleId = 1,
                BuyerPremiumPct = 5m,
                SellerFinalValuePct = 8m,
                ListingFee = 0m,
                EffectiveFrom = _now,
            });
            await _db.SaveChangesAsync(ct);
        }
    }

    // ---------- items and auctions ----------

    private record DemoAuction(string Key, string Seller, string CategorySlug, string Title, decimal StartingPrice,
        AuctionStatus Status, TimeSpan StartsIn, TimeSpan EndsIn);

    // All start Scheduled; the lifecycle leader loads and opens them (an auction made Live in SQL alone would be frozen by recovery).
    private static readonly DemoAuction[] DemoAuctions =
    [
        new("demo-1", "seller1", "watches-vintage", "1960s Omega Seamaster", 150m, AuctionStatus.Scheduled, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(12)),
        new("demo-2", "seller2", "art-prints", "Signed city skyline print", 40m, AuctionStatus.Scheduled, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(12)),
        new("demo-3", "seller3", "collectibles-coins", "1909 Lincoln cent", 25m, AuctionStatus.Scheduled, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(62)),
        new("demo-4", "seller1", "watches-modern", "Seiko diver, boxed", 90m, AuctionStatus.Scheduled, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(62)),
        new("demo-5", "seller2", "art-paintings", "Small oil landscape", 300m, AuctionStatus.Scheduled, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(65)),
        new("demo-6", "seller3", "collectibles-cards", "Rookie card, graded 9", 60m, AuctionStatus.Scheduled, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(65)),
    ];

    private async Task SeedAuctionsAsync(CancellationToken ct)
    {
        foreach (var demo in DemoAuctions)
        {
            var auctionId = StableId("auction:" + demo.Key);
            var exists = await _db.Auctions.AnyAsync(a => a.AuctionId == auctionId, ct);
            if (exists)
            {
                continue;
            }

            var seller = await _db.Users.FirstAsync(u => u.Username == demo.Seller, ct);
            var category = await _db.Categories.FirstAsync(c => c.Slug == demo.CategorySlug, ct);
            var itemId = StableId("item:" + demo.Key);

            var itemExists = await _db.Items.AnyAsync(i => i.ItemId == itemId, ct);
            if (!itemExists)
            {
                _db.Items.Add(new Item
                {
                    ItemId = itemId,
                    SellerId = seller.UserId,
                    CategoryId = category.CategoryId,
                    Title = demo.Title,
                    Description = "Demo item for the auction platform.",
                    Condition = ItemCondition.Good,
                    WarrantyType = WarrantyType.None,
                    ShippingType = ShippingType.BuyerPays,
                    ShippingCost = 10m,
                    HandlingDays = 2,
                    ReturnsAccepted = false,
                    Status = ItemStatus.InAuction,
                    CreatedAt = _now,
                    UpdatedAt = _now,
                });
            }

            var start = _now + demo.StartsIn;
            var end = _now + demo.EndsIn;
            _db.Auctions.Add(new AuctionEntity
            {
                AuctionId = auctionId,
                ItemId = itemId,
                SellerId = seller.UserId,
                Currency = "USD",
                StartTime = start,
                ScheduledEndTime = end,
                EndTime = end,
                StartingPrice = demo.StartingPrice,
                IncrementTableId = 1,
                FeeScheduleId = 1,
                Status = demo.Status,
                CreatedAt = _now,
                UpdatedAt = _now,
            });

            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>The 6 original demo items and what their drawn photos show.</summary>
    public static IEnumerable<(Guid ItemId, PictureKind Kind, string Key)> DemoItemPictures() =>
        DemoAuctions.Select(d => (StableId("item:" + d.Key), d.CategorySlug switch
        {
            var s when s.StartsWith("watches") => PictureKind.Watch,
            "art-paintings" => PictureKind.Painting,
            "art-prints" => PictureKind.Print,
            "collectibles-coins" => PictureKind.Coin,
            _ => PictureKind.Card,
        }, d.Key));

    // ---------- helpers ----------

    /// <summary>The same name always gives the same Guid, so reruns find the row again.</summary>
    public static Guid StableId(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("auction-seed:" + name));
        return new Guid(hash);
    }
}
