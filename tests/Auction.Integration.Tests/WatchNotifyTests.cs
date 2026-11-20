using Auction.Domain.Bidding;
using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Notify;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Infrastructure.Watch;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 07 tasks 1 to 4 on a real SQL Server and Redis. Streams are the test's own ({p97x}:stream and
/// an outbox stream with a new name), so a running workers copy never takes these entries.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class WatchNotifyTests(SqlServerFixture sql, RedisFixture redis)
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a7");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b7");
    private static readonly Guid Carol = Guid.Parse("cccccccc-0000-0000-0000-0000000000c7");

    private IDatabase Redis => redis.Connection.GetDatabase();

    private NotificationStore Store(LifecycleTestDb t) => new(t.Connections, redis.Connection, new MemoryCache(new MemoryCacheOptions()));

    private WatchManager Watches(LifecycleTestDb t) => new(t.Db, new ScriptRegistry(redis.Connection), RedisOptions.Default,
        new AuctionBrowser(new AuctionQueries(t.Db), new LiveStateReader(redis.Connection), t.Db, NullLogger<AuctionBrowser>.Instance),
        TimeProvider.System, NullLogger<WatchManager>.Instance);

    private async Task<int> RowsAsync(LifecycleTestDb t, Guid user, NotificationType? type = null)
    {
        await using var db = t.NewContext();
        return await db.Notifications.CountAsync(n => n.UserId == user && (type == null || n.Type == type));
    }

    private async Task BidAsync(AuctionKeys keys, Guid user, long maxC)
    {
        var outcome = await new BidScript(new ScriptRegistry(redis.Connection))
            .RunAsync(keys, user, maxC, BidAmountRules.Fingerprint(user, maxC, "USD"), Guid.NewGuid(), "USD", "t");
        outcome.Accepted.Should().BeTrue();
    }

    /// <summary>A Live auction in SQL and in partition {pN}, with a fresh stream and a started notifier.</summary>
    private async Task<(Guid Id, AuctionKeys Keys, Notifier Notifier)> LiveAsync(LifecycleTestDb t, int partition)
    {
        var a = await t.AddAuctionAsync(new(AuctionStatus.Live));
        await Redis.KeyDeleteAsync($"{{p{partition}}}:stream");
        var keys = await LiveAuctionSeeder.SeedAsync(Redis, new(a.AuctionId, LifecycleTestDb.Seller, StartC: 100, Inc: "0:100"), partition);
        var notifier = Notifier.ForPartition(redis.Connection, Store(t), NullLogger<Notifier>.Instance, partition, "test");
        await notifier.StartAsync();
        return (a.AuctionId, keys, notifier);
    }

    // ---------- task 1: watchlist ----------

    [Fact]
    public async Task Watching_twice_counts_once_and_unwatching_twice_gives_0()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "watch_twice");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Live));
        var keys = await LiveAuctionSeeder.SeedAsync(Redis, new(a.AuctionId, LifecycleTestDb.Seller));
        var watches = Watches(t);

        (await watches.WatchAsync(Alice, a.AuctionId)).Watchers.Should().Be(1);
        (await watches.WatchAsync(Alice, a.AuctionId)).Watchers.Should().Be(1, "watching again changes nothing");
        ((long)await Redis.HashGetAsync(keys.State, "watchers")).Should().Be(1);

        (await watches.UnwatchAsync(Alice, a.AuctionId)).Watchers.Should().Be(0);
        (await watches.UnwatchAsync(Alice, a.AuctionId)).Watchers.Should().Be(0);
        ((long)await Redis.HashGetAsync(keys.State, "watchers")).Should().Be(0);
        await LiveAuctionSeeder.CleanUpAsync(Redis, keys);
    }

    [Fact]
    public async Task Ended_auctions_cannot_be_watched()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "watch_ended");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Settled));

        var watch = () => Watches(t).WatchAsync(Alice, a.AuctionId);

        (await watch.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Watched_list_has_live_and_scheduled_only_ending_soonest_first()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "watch_list");
        var later = await t.AddAuctionAsync(new(AuctionStatus.Live, EndsInMs: 7_200_000));
        var sooner = await t.AddAuctionAsync(new(AuctionStatus.Scheduled, StartsInMs: 60_000, EndsInMs: 3_600_000));
        var ended = await t.AddAuctionAsync(new(AuctionStatus.Sealed));
        var watches = Watches(t);
        foreach (var id in new[] { later.AuctionId, sooner.AuctionId })
        {
            await watches.WatchAsync(Alice, id);
        }

        t.Db.Watchlist.Add(new WatchlistEntry { UserId = Alice, AuctionId = ended.AuctionId, CreatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();

        var list = await watches.ListWatchedAsync(Alice, Paging.From(1, 20));

        list.Rows.Select(r => r.AuctionId).Should().Equal(sooner.AuctionId, later.AuctionId);
        list.Total.Should().Be(2);
    }

    [Fact]
    public async Task Load_sets_the_watcher_count_from_sql()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "watch_load");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Scheduled, StartsInMs: 60_000));
        t.Db.Watchlist.AddRange(
            new WatchlistEntry { UserId = Alice, AuctionId = a.AuctionId, CreatedAt = DateTime.UtcNow },
            new WatchlistEntry { UserId = Bob, AuctionId = a.AuctionId, CreatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();

        var row = (await new LifecycleSql(t.Connections).ScheduledStartingByAsync(DateTime.UtcNow.AddMinutes(5), 10)).Single();
        var keys = new AuctionKeys(a.AuctionId, 972);
        await new LifecycleScripts(new ScriptRegistry(redis.Connection)).LoadAsync(keys, row.ToLoad("0:100"));

        row.Watchers.Should().Be(2);
        ((long)await Redis.HashGetAsync(keys.State, "watchers")).Should().Be(2);
        await Redis.KeyDeleteAsync(keys.State);
        await Redis.SortedSetRemoveAsync(keys.Index.Starts, a.AuctionId.ToString());
    }

    // ---------- task 3: notifier on the partition stream ----------

    [Fact]
    public async Task Outbid_notifies_only_the_previous_leader_and_pushes_to_the_bell()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "notify_outbid");
        var (_, keys, notifier) = await LiveAsync(t, 973);
        var pushed = new List<string>();
        var channel = await redis.Connection.GetSubscriber().SubscribeAsync(RedisChannel.Literal(NotificationStore.Channel(Alice)));
        channel.OnMessage(m => { lock (pushed) { pushed.Add(m.Message.ToString()); } });

        await BidAsync(keys, Alice, 1000);   // first bid: nobody outbid
        await BidAsync(keys, Bob, 5000);     // Bob takes the lead from Alice
        await notifier.RunOnceAsync();

        (await RowsAsync(t, Alice, NotificationType.Outbid)).Should().Be(1);
        (await RowsAsync(t, Bob)).Should().Be(0, "nobody is told about their own bid");
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && pushed.Count == 0)
        {
            await Task.Delay(50);
        }

        // Read the title from the JSON: the serializer writes ' as \u0027, so the raw text never matches.
        var message = pushed.Should().ContainSingle("the bell gets it within 2 s (R3.1)").Which;
        System.Text.Json.JsonDocument.Parse(message).RootElement.GetProperty("title").GetString().Should().StartWith("You've been outbid");
        await channel.UnsubscribeAsync();
    }

    [Fact]
    public async Task Closing_soon_is_one_notification_even_when_sent_again()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "notify_soon");
        var (id, keys, notifier) = await LiveAsync(t, 974);
        t.Db.Watchlist.Add(new WatchlistEntry { UserId = Carol, AuctionId = id, CreatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();
        await BidAsync(keys, Alice, 1000);
        t.Db.Bids.Add(new Bid { AuctionId = id, UserId = Alice, ESeq = 1, Seq = 1, Kind = BidKind.Manual, Amount = 1m, MaxAmount = 10m, AcceptedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();

        // As if an extension made the lifecycle say "closing soon" twice.
        foreach (var seq in new[] { 2, 3 })
        {
            await Redis.StreamAddAsync(keys.Stream, [new("type", "State"), new("auction", id.ToString()), new("status", "Live"),
                new("change", "ClosingSoon"), new("seq", seq), new("eseq", seq), new("at", 1)]);
        }

        await notifier.RunOnceAsync();

        (await RowsAsync(t, Carol, NotificationType.EndingSoon)).Should().Be(1, "watchers are told once");
        (await RowsAsync(t, Alice, NotificationType.EndingSoon)).Should().Be(1, "bidders are told once");
    }

    [Fact]
    public async Task A_restart_before_ack_repeats_nothing()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "notify_restart");
        var (id, keys, notifier) = await LiveAsync(t, 975);
        await BidAsync(keys, Alice, 1000);
        await BidAsync(keys, Bob, 5000);

        // The copy read the entries and saved the notification, then died before XACK.
        var read = await Redis.StreamReadGroupAsync(keys.Stream, Notifier.Group, "test", ">", 100);
        await Store(t).SaveAndPublishAsync(NotificationPlanner.Plan(
            NotificationPlanner.FromStream(read[^1].Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString()))!,
            (await Store(t).AuctionAsync(id))!,
            new Recipients(new HashSet<Guid>(), new HashSet<Guid>())));
        (await RowsAsync(t, Alice, NotificationType.Outbid)).Should().Be(1);

        await notifier.RunOnceAsync();   // after the restart: its pending entries come back first
        await notifier.RunOnceAsync();

        (await RowsAsync(t, Alice, NotificationType.Outbid)).Should().Be(1, "the event key stops a second row");
        (await Redis.StreamPendingAsync(keys.Stream, Notifier.Group)).PendingMessageCount.Should().Be(0);
    }

    // ---------- task 2 and 3: outbox relay and the settled notifications ----------

    [Fact]
    public async Task Settle_gives_won_lost_and_sold_through_the_outbox_once_even_after_a_relay_crash()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "notify_settle");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Sealed, CurrentPrice: 1250m, Leader: Bob, BidCount: 2));
        foreach (var (user, eseq) in new[] { (Alice, 1L), (Bob, 2L) })
        {
            t.Db.Bids.Add(new Bid { AuctionId = a.AuctionId, UserId = user, ESeq = eseq, Seq = eseq, Kind = BidKind.Manual, Amount = 1000m, MaxAmount = 1300m, AcceptedAt = DateTime.UtcNow });
        }

        await t.Db.SaveChangesAsync();
        await new Settler(t.Connections, NullLogger<Settler>.Instance).SettleAsync(t.Grant, a.AuctionId);

        var stream = $"outbox-test-{Guid.NewGuid():N}";
        var relay = new OutboxRelay(t.Connections, redis.Connection, NullLogger<OutboxRelay>.Instance) { StreamKey = stream };
        relay.BeforeMarkForTests = () => throw new IOException("crash after XADD");
        var crash = () => relay.RunOnceAsync();
        await crash.Should().ThrowAsync<IOException>();

        relay.BeforeMarkForTests = null;
        (await relay.RunOnceAsync()).Should().Be(1, "the row was not marked, so it goes again");
        (await relay.RunOnceAsync()).Should().Be(0, "now it is marked");
        (await Redis.StreamLengthAsync(stream)).Should().Be(2, "sent twice: once before the crash, once after");

        var notifier = Notifier.ForOutbox(redis.Connection, Store(t), NullLogger<Notifier>.Instance, "test", stream);
        await notifier.StartAsync();
        await notifier.RunOnceAsync();

        (await RowsAsync(t, Bob, NotificationType.Won)).Should().Be(1, "the repeat makes no second row");
        (await RowsAsync(t, Alice, NotificationType.Lost)).Should().Be(1);
        (await RowsAsync(t, LifecycleTestDb.Seller, NotificationType.AuctionSold)).Should().Be(1);
        await using var db = t.NewContext();
        (await db.Outbox.CountAsync(o => o.PublishedAt == null)).Should().Be(0);
        await Redis.KeyDeleteAsync(stream);
    }

    // ---------- task 4: reads ----------

    [Fact]
    public async Task List_mark_read_and_unread_count()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "notify_reads");
        var store = Store(t);
        var a = Guid.NewGuid();
        var saved = await store.SaveAndPublishAsync(
        [
            new(Alice, NotificationType.Outbid, "k1", "one", null, "/a/" + a, a),
            new(Alice, NotificationType.Won, "k2", "two", null, "/me/purchases", a),
            new(Alice, NotificationType.Won, "k2", "two again", null, "/me/purchases", a),   // same key: dropped
            new(Bob, NotificationType.Lost, "k3", "bob's", null, "/a/" + a, a),
        ]);

        saved.Should().HaveCount(3);
        (await store.UnreadCountAsync(Alice)).Should().Be(2);
        (await store.SaveAndPublishAsync([new(Alice, NotificationType.Outbid, "k1", "one", null, "/a/" + a, a)])).Should().BeEmpty("saved before");

        var page = await store.ListAsync(Alice, unreadOnly: false, Paging.From(1, 10));
        page.Items.Select(i => i.Title).Should().BeEquivalentTo(["one", "two"]);
        page.Unread.Should().Be(2);

        var oneId = page.Items.Single(i => i.Title == "one").NotificationId;
        (await store.MarkReadAsync(Alice, [oneId])).Should().Be(1);
        (await store.MarkReadAsync(Bob, [oneId])).Should().Be(1, "nobody can mark someone else's");
        (await store.ListAsync(Alice, unreadOnly: true, Paging.From(1, 10))).Items.Single().Title.Should().Be("two");

        (await store.MarkAllReadAsync(Alice)).Should().Be(0);
        (await store.UnreadCountAsync(Alice)).Should().Be(0);
        (await store.UnreadCountAsync(Bob)).Should().Be(1);
    }
}
