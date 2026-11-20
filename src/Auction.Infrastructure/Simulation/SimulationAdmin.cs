using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Infrastructure.Simulation;

public sealed record DemoAuctionSpec(
    int Count,
    TimeSpan StartsIn,
    TimeSpan Duration,
    string Seller,
    int SnipeWindowSec,
    int ExtensionSec,
    int MaxTotalExtensionSec,
    string TitlePrefix);

/// <summary>
/// Admin helpers for the simulator (spec 08 task 1, R1.1): bot users and demo auctions.
/// Bots are normal Bidder accounts marked IsBot, so reports can leave them out (R1.3).
/// </summary>
public sealed class SimulationAdmin(AuctionDbContext db, AuthManager auth, ImageStoreOptions images, TimeProvider clock)
{
    public const int MaxBots = 1000;
    public const int MaxDemoAuctions = 100;

    public static string BotName(int n) => $"bot_{n:D4}";

    /// <summary>Makes bot_0001..bot_{count} where missing, then signs each one in.</summary>
    public async Task<IReadOnlyList<AuthResult>> EnsureBotsAsync(int count, string? ip, CancellationToken ct = default)
    {
        if (count is < 1 or > MaxBots)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"Ask for 1 to {MaxBots} bots.",
                [new FieldIssue("count", "range", $"Must be 1 to {MaxBots}.")]);
        }

        var names = Enumerable.Range(1, count).Select(BotName).ToList();
        var existing = await db.Users.Where(u => names.Contains(u.Username)).ToDictionaryAsync(u => u.Username, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        // Bots never sign in with a password, so one hash with an unknown password is enough (and cheap).
        var hash = BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)), 4);
        foreach (var name in names.Where(n => !existing.ContainsKey(n)))
        {
            var user = new User
            {
                UserId = Guid.NewGuid(), Username = name, Email = $"{name}@bots.local", EmailVerifiedAt = now, PasswordHash = hash,
                Status = UserStatus.Active, IsBot = true, CreatedAt = now, UpdatedAt = now,
            };
            user.Roles.Add(new UserRole { UserId = user.UserId, Role = Role.Bidder, GrantedAt = now });
            db.Users.Add(user);
            existing[name] = user;
        }

        var people = existing.Values.Where(u => !u.IsBot).Select(u => u.Username).ToList();
        if (people.Count > 0)
        {
            throw new DomainException(ErrorCodes.InvalidState, $"These names belong to people, not bots: {string.Join(", ", people)}.");
        }

        await db.SaveChangesAsync(ct);
        // In name order (bot_0001 first), so callers and tests get the same order every time.
        return (await auth.IssueForBotsAsync(existing.Values.Select(u => u.UserId).ToList(), ip, ct))
            .OrderBy(r => r.User.Username, StringComparer.Ordinal).ToList();
    }

    /// <summary>Scheduled auctions that all end together, each with one drawn photo (design 1).</summary>
    public async Task<IReadOnlyList<Guid>> CreateDemoAuctionsAsync(DemoAuctionSpec spec, CancellationToken ct = default)
    {
        if (spec.Count is < 1 or > MaxDemoAuctions)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"Ask for 1 to {MaxDemoAuctions} auctions.",
                [new FieldIssue("count", "range", $"Must be 1 to {MaxDemoAuctions}.")]);
        }

        if (spec.StartsIn < TimeSpan.FromSeconds(5) || spec.StartsIn > TimeSpan.FromHours(1)
            || spec.Duration < TimeSpan.FromMinutes(1) || spec.Duration > TimeSpan.FromDays(1))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Start in 5 s to 1 h, and run 1 minute to 1 day.",
                [new FieldIssue("duration_sec", "range", "Out of range.")]);
        }

        var seller = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == spec.Seller, ct)
                     ?? throw new DomainException(ErrorCodes.NotFound, $"Seller {spec.Seller} not found. Run the seed first.");
        var categories = await db.Categories.AsNoTracking().Where(c => c.ParentCategoryId != null).Select(c => new { c.CategoryId, c.Slug }).ToListAsync(ct);
        if (categories.Count == 0)
        {
            throw new DomainException(ErrorCodes.InvalidState, "No categories yet. Run the seed first.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var start = now + spec.StartsIn;
        var end = start + spec.Duration;
        var batch = now.ToString("HHmmss");
        var ids = new List<Guid>();
        for (var i = 0; i < spec.Count; i++)
        {
            var category = categories[i % categories.Count];
            var item = new Item
            {
                ItemId = Guid.NewGuid(), SellerId = seller.UserId, CategoryId = category.CategoryId,
                Title = $"{spec.TitlePrefix} {batch}-{i + 1:D2}", Description = "Made by the load simulator.",
                Condition = ItemCondition.Good, ShippingType = ShippingType.FreeShipping, Status = ItemStatus.InAuction,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Items.Add(item);
            await DemoCatalog.WritePicturesAsync(db, images.RootPath, item.ItemId, KindFor(category.Slug), $"sim:{item.ItemId}", now, ct, count: 1);

            var auction = new AuctionEntity
            {
                AuctionId = Guid.NewGuid(), ItemId = item.ItemId, SellerId = seller.UserId, Currency = "USD",
                StartTime = start, ScheduledEndTime = end, EndTime = end, StartingPrice = 10m,
                IncrementTableId = AuctionRules.DefaultIncrementTableId, FeeScheduleId = AuctionRules.DefaultFeeScheduleId,
                SnipeWindowSec = spec.SnipeWindowSec, ExtensionSec = spec.ExtensionSec, MaxTotalExtensionSec = spec.MaxTotalExtensionSec,
                Status = AuctionStatus.Scheduled, CreatedAt = now, UpdatedAt = now,
            };
            db.Auctions.Add(auction);
            ids.Add(auction.AuctionId);
        }

        await db.SaveChangesAsync(ct);
        return ids;
    }

    public static PictureKind KindFor(string slug) => slug switch
    {
        var s when s.StartsWith("watches", StringComparison.Ordinal) => PictureKind.Watch,
        "art-paintings" => PictureKind.Painting,
        "art-prints" => PictureKind.Print,
        "collectibles-coins" => PictureKind.Coin,
        _ => PictureKind.Card,
    };
}
