using System.Diagnostics;
using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Simulator;

/// <summary>
/// Spec 05 task 7: leader failover check on the full stack (2 workers copies).
///
///   dotnet run --project src/Auction.Simulator -- failover-check [stop|kill]
///
/// 1. Creates a Scheduled auction that starts in 15 s and runs 60 s (seller1, seeded data).
/// 2. Waits until the lifecycle leader opens it, then places 3 bids straight into bid.lua.
/// 3. Stops (or kills) the workers copy that holds the lease, with docker compose.
/// 4. Waits for the other copy to take the lease, seal and settle the auction.
/// 5. Checks: new holder, higher fencing token, one Sealed event, one Settled event, one Settlement.
/// 6. Starts the stopped copy again.
///
/// Needs ConnectionStrings__Sql and ConnectionStrings__Redis pointing at the stack (localhost:1433, localhost:6379),
/// and is run from the repo root (it calls docker compose -f deploy/compose.yml).
/// </summary>
public static class FailoverCheck
{
    private static readonly TimeSpan StartsIn = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RunsFor = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(string[] args)
    {
        var mode = args.Length > 1 && args[1] == "kill" ? "kill" : "stop";
        var sqlText = Environment.GetEnvironmentVariable("ConnectionStrings__Sql")
                      ?? throw new InvalidOperationException("Set ConnectionStrings__Sql (for example Server=localhost,1433;Database=Auction;...).");
        var redisText = Environment.GetEnvironmentVariable("ConnectionStrings__Redis") ?? "localhost:6379";
        var partitions = int.TryParse(Environment.GetEnvironmentVariable("Redis__Partitions"), out var p) ? p : 1;

        await using var db = new AuctionDbContext(new DbContextOptionsBuilder<AuctionDbContext>().UseSqlServer(sqlText).Options);
        using var redis = await ConnectionMultiplexer.ConnectAsync(redisText);
        var r = redis.GetDatabase();

        // ---- 1. the auction ----
        var (holderBefore, tokenBefore) = await LeaseAsync(sqlText);
        if (string.IsNullOrEmpty(holderBefore))
        {
            return Fail("No workers copy holds the lifecycle lease. Is the full stack running?");
        }

        Say($"Lease held by {Instance(holderBefore)} (token {tokenBefore}).");
        var auctionId = await CreateAuctionAsync(db);
        var keys = RedisKeys.For(auctionId, partitions);
        Say($"Created auction {auctionId}: starts in {StartsIn.TotalSeconds:F0} s, runs {RunsFor.TotalSeconds:F0} s.");

        // ---- 2. open and bid ----
        if (!await WaitAsync(async () => (string?)await r.HashGetAsync(keys.State, "status") == "Live", StartsIn + TimeSpan.FromSeconds(20)))
        {
            return Fail("The auction did not open. Check the workers logs in Seq.");
        }

        Say("Opened. Placing 3 bids...");
        var bidders = await db.Users.AsNoTracking().Where(u => u.Username == "alice" || u.Username == "bob")
            .Select(u => u.UserId).ToListAsync();
        while (bidders.Count < 2)
        {
            bidders.Add(Guid.NewGuid());
        }

        var script = new BidScript(new ScriptRegistry(redis));
        long[] maxes = [2000, 3500, 6000];
        for (var i = 0; i < maxes.Length; i++)
        {
            var user = bidders[i % 2];
            var outcome = await script.RunAsync(keys, user, maxes[i], BidAmountRules.Fingerprint(user, maxes[i], "USD"), Guid.NewGuid(), "USD", "failover-check");
            Say($"  bid {i + 1}: max {maxes[i] / 100m:F2} -> {(outcome.Accepted ? outcome.Result.ToString() : outcome.ErrorCode)}");
        }

        // ---- 3. stop the leader mid-auction ----
        var (holderNow, _) = await LeaseAsync(sqlText);
        var service = Instance(holderNow);
        Say($"Running 'docker compose {mode} {service}' while the auction is still Live...");
        if (await ComposeAsync(mode, service) != 0)
        {
            return Fail($"docker compose {mode} {service} failed.");
        }

        var stoppedAt = Stopwatch.StartNew();
        try
        {
            // ---- 4. takeover, seal and settle ----
            if (!await WaitAsync(async () => (await LeaseAsync(sqlText)).Holder is var h && h != holderNow && h != "", TimeSpan.FromSeconds(30)))
            {
                return Fail("No other copy took the lease within 30 s (R1.2 says 20 s).");
            }

            var (holderAfter, tokenAfter) = await LeaseAsync(sqlText);
            Say($"Lease taken by {Instance(holderAfter)} (token {tokenAfter}) after {stoppedAt.Elapsed.TotalSeconds:F1} s.");

            Say("Waiting for seal and settle...");
            var settled = await WaitAsync(
                async () => await db.Auctions.AsNoTracking().Where(a => a.AuctionId == auctionId).Select(a => a.Status).SingleAsync() == AuctionStatus.Settled,
                RunsFor + TimeSpan.FromSeconds(60));
            if (!settled)
            {
                return Fail("The auction was not settled in time.");
            }

            // ---- 5. checks ----
            var changes = (await r.StreamRangeAsync(keys.Stream))
                .Where(e => e["auction"] == auctionId.ToString() && e["type"] == "State")
                .Select(e => e["change"].ToString()).ToList();
            var sealedEvents = changes.Count(c => c == "Sealed");
            var settledEvents = changes.Count(c => c == "Settled");
            var settlements = await db.Settlements.CountAsync(s => s.AuctionId == auctionId);
            var outcomeRow = await db.Auctions.AsNoTracking().Where(a => a.AuctionId == auctionId).Select(a => new { a.Outcome, a.CurrentPrice }).SingleAsync();

            Say($"Outcome {outcomeRow.Outcome} at {outcomeRow.CurrentPrice:F2}; events: {string.Join(", ", changes)}.");
            var checks = new (string Name, bool Ok)[]
            {
                ("another copy holds the lease", Instance(holderAfter) != service),
                ("fencing token went up", tokenAfter > tokenBefore),
                ("one Sealed event", sealedEvents == 1),
                ("one Settled event", settledEvents == 1),
                ("one Settlement row", settlements == 1),
            };
            foreach (var (name, ok) in checks)
            {
                Say($"  {(ok ? "PASS" : "FAIL")}  {name}");
            }

            var pass = checks.All(c => c.Ok);
            Say(pass ? "PASS: the auction sealed and settled once after the leader stopped." : "FAIL: see the lines above.");
            return pass ? 0 : 1;
        }
        finally
        {
            // ---- 6. put the stack back ----
            Say($"Starting {service} again.");
            await ComposeAsync("start", service);
        }
    }

    private static async Task<Guid> CreateAuctionAsync(AuctionDbContext db)
    {
        var seller = await db.Users.AsNoTracking().Where(u => u.Username == "seller1").Select(u => u.UserId).FirstOrDefaultAsync();
        if (seller == Guid.Empty)
        {
            throw new InvalidOperationException("Seed data is missing (no seller1). Run the migrator with seeding first.");
        }

        var categoryId = await db.Categories.AsNoTracking().OrderBy(c => c.CategoryId).Select(c => c.CategoryId).FirstAsync();
        var now = DateTime.UtcNow;
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = seller, CategoryId = categoryId, Title = "Failover check " + now.ToString("HH:mm:ss"),
            Description = "Made by the spec 05 failover check.", Condition = ItemCondition.New, ShippingType = ShippingType.FreeShipping,
            Status = ItemStatus.InAuction, CreatedAt = now, UpdatedAt = now,
        };
        var start = now + StartsIn;
        var end = start + RunsFor;
        var auction = new AuctionEntity
        {
            AuctionId = Guid.NewGuid(), ItemId = item.ItemId, SellerId = seller, StartTime = start, ScheduledEndTime = end, EndTime = end,
            StartingPrice = 10m, FeeScheduleId = 1, IncrementTableId = 1, SnipeWindowSec = 0, ExtensionSec = 0, MaxTotalExtensionSec = 0,
            Status = AuctionStatus.Scheduled, CreatedAt = now, UpdatedAt = now,
        };
        db.Items.Add(item);
        db.Auctions.Add(auction);
        await db.SaveChangesAsync();
        return auction.AuctionId;
    }

    private static async Task<(string Holder, long Token)> LeaseAsync(string sql)
    {
        await using var connection = new SqlConnection(sql);
        return await connection.QuerySingleAsync<(string, long)>(
            "SELECT Holder, FencingToken FROM bidding.LifecycleLeases WHERE LeaseName = 'lifecycle' AND ExpiresAt > SYSUTCDATETIME() " +
            "UNION ALL SELECT '', 0 WHERE NOT EXISTS (SELECT 1 FROM bidding.LifecycleLeases WHERE LeaseName = 'lifecycle' AND ExpiresAt > SYSUTCDATETIME())");
    }

    /// <summary>"workers-1:3f2a..." -> "workers-1" (the compose service name).</summary>
    private static string Instance(string holder) => holder.Split(':')[0];

    private static async Task<int> ComposeAsync(string verb, string service)
    {
        var start = new ProcessStartInfo("docker")
        {
            ArgumentList = { "compose", "--env-file", ".env", "-f", "deploy/compose.yml", verb, service },
            UseShellExecute = false,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start docker.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> done, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (await done())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private static void Say(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");

    private static int Fail(string text)
    {
        Say("FAIL: " + text);
        return 1;
    }
}
