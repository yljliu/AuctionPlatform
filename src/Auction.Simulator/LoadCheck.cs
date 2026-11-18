using System.Diagnostics;
using Auction.Domain.Bidding;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Redis;
using StackExchange.Redis;

namespace Auction.Simulator;

/// <summary>
/// Spec 03 task 9: send 500 bids a second for 60 seconds across 20 auctions straight into bid.lua,
/// while the Workers persister runs, and check that persist_lag_ms stays under 2 seconds (R8.6).
///
///   dotnet run --project src/Auction.Simulator -- load-check [bidsPerSecond] [seconds] [auctions]
///
/// Needs ConnectionStrings__Redis. The auctions exist only in Redis (no SQL rows), so their bids
/// are written to bidding.Bids but no catalog row changes.
/// </summary>
public static class LoadCheck
{
    private const string Table1 = "0:100,5000:250,25000:500,100000:1000,500000:5000";

    public static async Task<int> RunAsync(string[] args)
    {
        var rate = args.Length > 1 ? int.Parse(args[1]) : 500;
        var seconds = args.Length > 2 ? int.Parse(args[2]) : 60;
        var auctionCount = args.Length > 3 ? int.Parse(args[3]) : 20;
        var redisText = Environment.GetEnvironmentVariable("ConnectionStrings__Redis") ?? "localhost:6379";

        using var redis = await ConnectionMultiplexer.ConnectAsync(redisText);
        var db = redis.GetDatabase();
        var script = new BidScript(new ScriptRegistry(redis));

        var auctions = new List<AuctionKeys>();
        for (var i = 0; i < auctionCount; i++)
        {
            auctions.Add(await SeedAsync(db, Guid.NewGuid(), seconds));
        }

        Console.WriteLine($"Load check: {rate} bids/s for {seconds} s over {auctionCount} auctions");
        var lagReader = new LagReader(db, "{p0}:stream");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var sent = 0L;
        var accepted = 0L;
        var maxLag = 0L;

        var lagTask = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var lag = await lagReader.ReadAsync();
                maxLag = Math.Max(maxLag, lag);
                Console.WriteLine($"  sent {Interlocked.Read(ref sent),7}  accepted {Interlocked.Read(ref accepted),7}  persist_lag_ms {lag,6}");
                try
                {
                    await Task.Delay(1000, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        // Send in 10 ms ticks to keep the rate steady.
        var perTick = Math.Max(1, rate / 100);
        var clock = Stopwatch.StartNew();
        var tick = 0L;
        var random = new Random(7);
        var users = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToArray();
        var maxes = new Dictionary<Guid, long>();

        while (!stop.IsCancellationRequested)
        {
            var work = new List<Task>();
            for (var i = 0; i < perTick; i++)
            {
                var keys = auctions[random.Next(auctions.Count)];
                var user = users[random.Next(users.Length)];
                var maxC = maxes.GetValueOrDefault(keys.AuctionId, 1000) + random.Next(100, 2000);
                maxes[keys.AuctionId] = maxC;
                work.Add(Task.Run(async () =>
                {
                    var outcome = await script.RunAsync(keys, user, maxC, BidAmountRules.Fingerprint(user, maxC, "USD"), Guid.NewGuid(), "USD", "load");
                    Interlocked.Increment(ref sent);
                    if (outcome.Accepted)
                    {
                        Interlocked.Increment(ref accepted);
                    }
                }));
            }

            await Task.WhenAll(work);
            tick++;
            var wait = (tick * 10) - clock.ElapsedMilliseconds;
            if (wait > 0)
            {
                await Task.Delay((int)wait);
            }
        }

        await lagTask;

        // Give the persister a moment to finish, then read the final lag.
        await Task.Delay(3000);
        var finalLag = await lagReader.ReadAsync();
        var achieved = sent / (double)Math.Max(1, seconds);
        Console.WriteLine($"Done. sent {sent} ({achieved:F0}/s), accepted {accepted}, max lag {maxLag} ms, lag after 3 s {finalLag} ms");

        var pass = maxLag < 2000 && achieved >= rate * 0.9;
        Console.WriteLine(pass ? "PASS: lag stayed under 2 s" : "FAIL: lag reached 2 s or the rate was not reached");
        return pass ? 0 : 1;
    }

    private static async Task<AuctionKeys> SeedAsync(IDatabase db, Guid auctionId, int seconds)
    {
        var keys = RedisKeys.For(auctionId, 1);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.HashSetAsync(keys.State,
        [
            new("status", "Live"), new("seller", Guid.NewGuid().ToString()), new("startMs", now - 60_000),
            new("endMs", now + ((seconds + 600) * 1000L)), new("startC", 1000), new("priceC", 0), new("leader", ""),
            new("leaderMaxC", 0), new("bidCount", 0), new("seq", 0), new("eseq", 0), new("persistedEseq", 0),
            new("snipeMs", 0), new("extMs", 0), new("maxExtMs", 0), new("extUsedMs", 0), new("inc", Table1), new("cur", "USD"),
        ]);
        await db.KeyExpireAsync(keys.State, TimeSpan.FromHours(1));
        return keys;
    }

    /// <summary>Same lag rule as the persister gauge, read from outside.</summary>
    private sealed class LagReader(IDatabase db, string stream)
    {
        public async Task<long> ReadAsync()
        {
            try
            {
                string? lowestPending = null;
                string? lastDelivered = null;
                foreach (var group in await db.StreamGroupInfoAsync(stream))
                {
                    if (group.Name != PersisterOptions.Group)
                    {
                        continue;
                    }

                    lastDelivered = group.LastDeliveredId;
                    if (group.PendingMessageCount > 0)
                    {
                        lowestPending = (await db.StreamPendingAsync(stream, PersisterOptions.Group)).LowestPendingMessageId;
                    }
                }

                if (lastDelivered == null)
                {
                    return -1;   // no persister group yet: start Workers first
                }

                var next = await db.StreamRangeAsync(stream, minId: "(" + lastDelivered, count: 1);
                return StreamMath.LagMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lowestPending, next.Length > 0 ? next[0].Id.ToString() : null);
            }
            catch (RedisServerException)
            {
                return -1;
            }
        }
    }
}
