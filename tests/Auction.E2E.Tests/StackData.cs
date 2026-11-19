using Auction.Domain.Enums;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.E2E.Tests;

/// <summary>Settings for the browser tests, from environment variables.</summary>
public static class E2ESettings
{
    public static string Web1 => Get("E2E_WEB1", "http://localhost:5001");

    public static string Web2 => Get("E2E_WEB2", Web1);

    public static string Redis => Get("E2E_REDIS", "localhost:6379");

    /// <summary>The stack's SQL database (with seed data), e.g. Server=localhost,1433;Database=Auction;User Id=sa;Password=...</summary>
    public static string Sql => Get("E2E_SQL", "");

    public static int Partitions => int.Parse(Get("E2E_PARTITIONS", "1"));

    private static string Get(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.TrimEnd('/');
    }
}

/// <summary>
/// Makes a Live auction that both the page (SQL) and bidding (Redis) know, the way spec 05 opens one.
/// Uses the seeded seller1 and the first category. Runs 10 minutes; the lifecycle loop then seals it.
/// </summary>
public static class StackData
{
    public static async Task<(Guid AuctionId, string Title, AuctionKeys Keys)> CreateLiveAuctionAsync(IConnectionMultiplexer redis, decimal startingPrice = 10m)
    {
        if (string.IsNullOrEmpty(E2ESettings.Sql))
        {
            throw new InvalidOperationException("Set E2E_SQL to the stack's database (see the test file header).");
        }

        await using var db = new AuctionDbContext(new DbContextOptionsBuilder<AuctionDbContext>().UseSqlServer(E2ESettings.Sql).Options);
        var seller = await db.Users.AsNoTracking().Where(u => u.Username == "seller1").Select(u => u.UserId).SingleAsync();
        var categoryId = await db.Categories.AsNoTracking().OrderBy(c => c.CategoryId).Select(c => c.CategoryId).FirstAsync();

        var now = DateTime.UtcNow;
        var end = now.AddMinutes(10);
        var title = $"E2E lamp {Guid.NewGuid():N}"[..20];
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = seller, CategoryId = categoryId, Title = title, Description = "Made by a browser test.",
            Condition = ItemCondition.Good, ShippingType = ShippingType.FreeShipping, Status = ItemStatus.InAuction, CreatedAt = now, UpdatedAt = now,
        };
        var auction = new AuctionEntity
        {
            AuctionId = Guid.NewGuid(), ItemId = item.ItemId, SellerId = seller, StartTime = now.AddMinutes(-1), ScheduledEndTime = end, EndTime = end,
            StartingPrice = startingPrice, FeeScheduleId = 1, IncrementTableId = 1, SnipeWindowSec = 0, ExtensionSec = 0, MaxTotalExtensionSec = 0,
            Status = AuctionStatus.Live, CreatedAt = now, UpdatedAt = now,
        };
        db.Items.Add(item);
        db.Auctions.Add(auction);
        await db.SaveChangesAsync();

        var keys = RedisKeys.For(auction.AuctionId, E2ESettings.Partitions);
        var startMs = new DateTimeOffset(auction.StartTime).ToUnixTimeMilliseconds();
        var endMs = new DateTimeOffset(end).ToUnixTimeMilliseconds();
        await redis.GetDatabase().HashSetAsync(keys.State,
        [
            new("status", "Live"), new("seller", seller.ToString()), new("startMs", startMs), new("endMs", endMs), new("schedEndMs", endMs),
            new("startC", (long)(startingPrice * 100)), new("reserveC", ""), new("priceC", 0), new("leader", ""), new("leaderMaxC", 0),
            new("bidCount", 0), new("seq", 0), new("eseq", 0), new("persistedEseq", 0), new("snipeMs", 0), new("extMs", 0),
            new("maxExtMs", 0), new("extUsedMs", 0), new("inc", "0:100,5000:250,25000:500,100000:1000,500000:5000"), new("cur", "USD"),
            new("watchers", 0),
        ]);
        await redis.GetDatabase().SortedSetAddAsync(keys.Index.Ends, auction.AuctionId.ToString(), endMs);
        return (auction.AuctionId, title, keys);
    }
}
