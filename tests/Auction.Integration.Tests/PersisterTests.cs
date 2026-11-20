using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 03 task 8: the stream persister on real SQL and Redis.
/// Each test uses its own partition ({p9xx}) and its own database, so nothing is shared.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class PersisterTests(SqlServerFixture sql, RedisFixture redis)
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Seller = Guid.Parse("55555555-0000-0000-0000-000000000005");

    private IDatabase Redis => redis.Connection.GetDatabase();

    private sealed record Setup(AuctionDbContext Db, string ConnectionString, AuctionKeys Keys, int Partition);

    /// <summary>A fresh database with one Live auction (SQL and Redis) in its own partition.</summary>
    private async Task<Setup> SetUpAsync(string name, int partition)
    {
        var database = "persist_" + name;
        var db = await sql.CreateEmptyContextAsync(database);
        await db.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        var category = new Category { Name = "c", Slug = "c" };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = Seller, CategoryId = category.CategoryId, Title = "t", Description = "d",
            ShippingType = ShippingType.FreeShipping, Status = ItemStatus.InAuction, CreatedAt = now, UpdatedAt = now,
        };
        var auction = new AuctionEntity
        {
            AuctionId = Guid.NewGuid(), ItemId = item.ItemId, SellerId = Seller, StartTime = now.AddHours(-1),
            ScheduledEndTime = now.AddHours(1), EndTime = now.AddHours(1), StartingPrice = 10, FeeScheduleId = 1,
            Status = AuctionStatus.Live, CreatedAt = now, UpdatedAt = now,
        };
        db.Items.Add(item);
        db.Auctions.Add(auction);
        await db.SaveChangesAsync();

        // Start from an empty stream for this partition.
        await Redis.KeyDeleteAsync([$"{{p{partition}}}:stream", $"{{p{partition}}}:dlq"]);
        var keys = await LiveAuctionSeeder.SeedAsync(Redis, new(auction.AuctionId, Seller, EndsInMs: 3_600_000), partition);

        return new Setup(db, sql.ConnectionStringFor(database), keys, partition);
    }

    private StreamPersister Persister(Setup s, string? connectionString = null, int maxAttempts = 10, string consumer = "test") =>
        new(redis.Connection, new ScriptRegistry(redis.Connection),
            new PersistSqlWriter(new SqlConnectionFactory(connectionString ?? s.ConnectionString)),
            new PersisterOptions { Consumer = consumer, MaxAttempts = maxAttempts },
            NullLogger<StreamPersister>.Instance, TimeProvider.System, s.Partition);

    private async Task BidAsync(AuctionKeys keys, Guid user, long maxC)
    {
        var outcome = await new BidScript(new ScriptRegistry(redis.Connection))
            .RunAsync(keys, user, maxC, BidAmountRules.Fingerprint(user, maxC, "USD"), Guid.NewGuid(), "USD", "test");
        outcome.Accepted.Should().BeTrue();
    }

    private static async Task DrainAsync(StreamPersister persister)
    {
        for (var i = 0; i < 20; i++)
        {
            var round = await persister.RunOnceAsync();
            if (round.Read == 0)
            {
                return;
            }
        }
    }

    private async Task<long> PendingAsync(Setup s) =>
        (await Redis.StreamPendingAsync(s.Keys.Stream, PersisterOptions.Group)).PendingMessageCount;

    // ---------- in order ----------

    [Fact]
    public async Task Entries_land_in_sql_in_order_and_are_acknowledged()
    {
        var s = await SetUpAsync("in_order", 901);
        await using var _ = s.Db;
        var persister = Persister(s);
        await persister.StartAsync();

        await BidAsync(s.Keys, Alice, 5000);   // eseq 1: Alice leads at 10.00
        await BidAsync(s.Keys, Bob, 3000);     // eseq 2: Alice's proxy answers at 31.00
        await BidAsync(s.Keys, Alice, 9000);   // eseq 3: max raise, no seq
        await DrainAsync(persister);

        var bids = await s.Db.Bids.AsNoTracking().OrderBy(b => b.ESeq).ThenBy(b => b.Idx).ToListAsync();
        bids.Select(b => (b.ESeq, b.Idx, b.Kind)).Should().Equal(
            (1L, (byte)0, BidKind.Manual), (2L, (byte)0, BidKind.AutoOutbid), (2L, (byte)1, BidKind.ProxyAuto), (3L, (byte)0, BidKind.MaxRaise));
        bids.Single(b => b.ESeq == 1).MaxAmount.Should().Be(50m);
        bids.Single(b => b.ESeq == 3).Seq.Should().BeNull();
        bids.Where(b => b.Kind == BidKind.ProxyAuto).Should().OnlyContain(b => b.MaxAmount == null);

        var auction = await s.Db.Auctions.AsNoTracking().SingleAsync();
        auction.CurrentPrice.Should().Be(31m);
        auction.CurrentLeaderId.Should().Be(Alice);
        auction.BidCount.Should().Be(2);
        auction.LastSeq.Should().Be(2);
        auction.LastESeq.Should().Be(3);
        (await s.Db.Items.AsNoTracking().SingleAsync()).LockedAt.Should().NotBeNull("the first bid locks the item (R9.1)");
        (await s.Db.AuctionEvents.CountAsync()).Should().Be(2, "one event per price change");

        ((long)await Redis.HashGetAsync(s.Keys.State, "persistedEseq")).Should().Be(3);
        (await PendingAsync(s)).Should().Be(0);
    }

    [Fact]
    public async Task An_old_auction_with_a_microsecond_end_still_persists()
    {
        // Verifier bug: SQL held the end to the microsecond, Redis to the millisecond, so the end the persister wrote
        // fell just below ScheduledEndTime and CK_Times refused every bid (dead-lettered, auction frozen).
        var s = await SetUpAsync("microsecond_end", 909);
        await using var _ = s.Db;
        var endMs = (long)await Redis.HashGetAsync(s.Keys.State, "endMs");
        var sqlEnd = DateTimeOffset.FromUnixTimeMilliseconds(endMs).UtcDateTime.AddTicks(5000);   // + 0.5 ms, as old rows had
        await s.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE catalog.Auctions SET ScheduledEndTime = {sqlEnd}, EndTime = {sqlEnd}");   // raw SQL: bypasses the ms cut
        var persister = Persister(s);
        await persister.StartAsync();

        await BidAsync(s.Keys, Alice, 5000);
        await DrainAsync(persister);

        (await Redis.StreamLengthAsync(s.Keys.DeadLetter)).Should().Be(0);
        (await s.Db.Bids.CountAsync()).Should().Be(1);
        var auction = await s.Db.Auctions.AsNoTracking().SingleAsync();
        auction.EndTime.Should().BeOnOrAfter(auction.ScheduledEndTime, "CK_Times");
        auction.Status.Should().Be(AuctionStatus.Live, "not frozen");
    }

    [Fact]
    public async Task New_auction_times_are_saved_to_the_whole_millisecond()
    {
        var s = await SetUpAsync("ms_times", 908);
        await using var _ = s.Db;

        var auction = await s.Db.Auctions.AsNoTracking().SingleAsync();

        (auction.StartTime.Ticks % TimeSpan.TicksPerMillisecond).Should().Be(0);
        (auction.ScheduledEndTime.Ticks % TimeSpan.TicksPerMillisecond).Should().Be(0);
        (auction.EndTime.Ticks % TimeSpan.TicksPerMillisecond).Should().Be(0);
    }

    // ---------- crash before ack ----------

    [Fact]
    public async Task Replay_after_a_crash_before_ack_adds_no_duplicates()
    {
        var s = await SetUpAsync("replay", 902);
        await using var _ = s.Db;
        var persister = Persister(s, consumer: "worker-1");
        await persister.StartAsync();
        await BidAsync(s.Keys, Alice, 5000);
        await BidAsync(s.Keys, Bob, 7000);

        // "Crash": read the entries and write them to SQL, but never acknowledge.
        var raw = await Redis.StreamReadGroupAsync(s.Keys.Stream, PersisterOptions.Group, "worker-1", ">", 100);
        var entries = raw.Select(e => PersistEntryParser.Parse(e.Id!, e.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString()))).ToList();
        await new PersistSqlWriter(new SqlConnectionFactory(s.ConnectionString)).WriteAsync(PersistPlanner.Build(entries));
        var rowsAfterCrash = await s.Db.Bids.CountAsync();
        (await PendingAsync(s)).Should().Be(2);

        // Restart: the same consumer reads its pending entries first and writes them again.
        var restarted = Persister(s, consumer: "worker-1");
        await restarted.StartAsync();
        await DrainAsync(restarted);

        (await s.Db.Bids.CountAsync()).Should().Be(rowsAfterCrash, "every row was already there");
        (await s.Db.AuctionEvents.CountAsync()).Should().Be(2);
        (await PendingAsync(s)).Should().Be(0);
    }

    [Fact]
    public async Task Entries_stuck_at_a_dead_consumer_are_taken_over()
    {
        var s = await SetUpAsync("claim", 903);
        await using var _ = s.Db;
        await Persister(s, consumer: "dead").StartAsync();
        await BidAsync(s.Keys, Alice, 5000);
        await Redis.StreamReadGroupAsync(s.Keys.Stream, PersisterOptions.Group, "dead", ">", 100);   // read, then "die"

        var taker = new StreamPersister(redis.Connection, new ScriptRegistry(redis.Connection),
            new PersistSqlWriter(new SqlConnectionFactory(s.ConnectionString)),
            new PersisterOptions { Consumer = "alive", ClaimIdleMs = 0 },
            NullLogger<StreamPersister>.Instance, TimeProvider.System, s.Partition);
        await taker.StartAsync();
        await DrainAsync(taker);

        (await s.Db.Bids.CountAsync()).Should().Be(1);
        (await PendingAsync(s)).Should().Be(0);
    }

    // ---------- SQL down ----------

    [Fact]
    public async Task Sql_down_means_no_ack_and_it_recovers_when_sql_is_back()
    {
        var s = await SetUpAsync("sql_down", 904);
        await using var _ = s.Db;
        const string unreachable = "Server=127.0.0.1,1;Database=x;User Id=sa;Password=x;Connect Timeout=1;TrustServerCertificate=True";

        var down = Persister(s, connectionString: unreachable, consumer: "worker-1");
        await down.StartAsync();
        await BidAsync(s.Keys, Alice, 5000);
        await BidAsync(s.Keys, Bob, 7000);

        var round = await down.RunOnceAsync();

        round.Written.Should().Be(0);
        round.WaitBefore.Should().BeGreaterThan(TimeSpan.Zero, "it backs off");
        round.DeadLettered.Should().Be(0, "SQL being down is not the entries' fault");
        (await PendingAsync(s)).Should().Be(2);
        (await Redis.StreamLengthAsync(s.Keys.DeadLetter)).Should().Be(0);

        var up = Persister(s, consumer: "worker-1");
        await up.StartAsync();
        await DrainAsync(up);

        (await s.Db.Bids.CountAsync()).Should().Be(3);
        (await PendingAsync(s)).Should().Be(0);
    }

    // ---------- poison ----------

    [Fact]
    public async Task Unreadable_entry_goes_to_the_dlq_and_freezes_the_auction()
    {
        var s = await SetUpAsync("poison_parse", 905);
        await using var _ = s.Db;
        var persister = Persister(s);
        await persister.StartAsync();

        await Redis.StreamAddAsync(s.Keys.Stream, [new("type", "Bid"), new("auction", s.Keys.AuctionId.ToString()), new("eseq", "1"), new("rows", "garbage")]);
        await DrainAsync(persister);

        (await Redis.StreamLengthAsync(s.Keys.DeadLetter)).Should().Be(1);
        ((string?)await Redis.HashGetAsync(s.Keys.State, "status")).Should().Be("Frozen");
        (await PendingAsync(s)).Should().Be(0);
    }

    [Fact]
    public async Task Entry_that_sql_always_refuses_is_tried_then_dead_lettered_and_others_still_land()
    {
        var s = await SetUpAsync("poison_sql", 906);
        await using var _ = s.Db;
        var persister = Persister(s, maxAttempts: 3);
        await persister.StartAsync();

        // A good bid on another auction, and an entry whose amount does not fit DECIMAL(19,4).
        var other = await LiveAuctionSeeder.SeedAsync(Redis, new(Guid.NewGuid(), Seller), s.Partition);
        await BidAsync(other, Bob, 5000);
        var huge = "9000000000000000000";
        await Redis.StreamAddAsync(s.Keys.Stream,
        [
            new("type", "Bid"), new("auction", s.Keys.AuctionId.ToString()), new("eseq", "1"), new("seq", "1"),
            new("user", Alice.ToString()), new("maxC", huge), new("result", "Leading"), new("priceC", huge),
            new("leader", Alice.ToString()), new("leaderMaxC", huge), new("bidCount", "1"), new("endMs", "1"),
            new("extended", "0"), new("rows", $"[{{\"seq\":1,\"user\":\"{Alice}\",\"amountC\":{huge},\"atMs\":1,\"kind\":\"Manual\"}}]"),
            new("outbid", ""), new("idem", Guid.NewGuid().ToString()), new("at", "1"), new("corr", "t"),
        ]);

        for (var i = 0; i < 10; i++)
        {
            await persister.RunOnceAsync();
        }

        (await Redis.StreamLengthAsync(s.Keys.DeadLetter)).Should().Be(1);
        var dead = (await Redis.StreamRangeAsync(s.Keys.DeadLetter)).Single();
        ((int)dead["dlqAttempts"]).Should().Be(3);
        ((string?)await Redis.HashGetAsync(s.Keys.State, "status")).Should().Be("Frozen");
        (await s.Db.Bids.CountAsync(b => b.AuctionId == other.AuctionId)).Should().Be(1, "the good entry was written on its own");
        (await PendingAsync(s)).Should().Be(0);
    }

    // ---------- trim and lag ----------

    [Fact]
    public async Task Trim_and_lag_work_on_a_persisted_stream()
    {
        var s = await SetUpAsync("trim_lag", 907);
        await using var _ = s.Db;
        var persister = Persister(s);
        await persister.StartAsync();
        await BidAsync(s.Keys, Alice, 5000);

        (await persister.MeasureLagAsync()).Should().BeGreaterOrEqualTo(0);
        await DrainAsync(persister);
        (await persister.MeasureLagAsync()).Should().Be(0, "everything is in SQL");

        var minId = await persister.TrimAsync();

        minId.Should().NotBeNull();
        (await Redis.StreamLengthAsync(s.Keys.Stream)).Should().Be(1, "entries from the last 10 minutes are kept");
    }
}
