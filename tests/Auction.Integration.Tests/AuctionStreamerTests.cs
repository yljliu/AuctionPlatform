using System.Threading.Channels;
using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Live;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;
using ProtoAuctionEvent = Auction.Contracts.AuctionEvent;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 04 task 4: SubscribeAuction with catch-up on a real Redis (partition {p0}).
/// A background loop runs the live reader, like the Api's LiveStreamWorker.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AuctionStreamerTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
{
    private AuctionDbContext _db = null!;
    private string _connectionString = "";
    private readonly Guid[] _bidders = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
    private readonly CancellationTokenSource _stop = new();
    private AuctionEventBus _bus = null!;
    private UserHandleCache _handles = null!;
    private Task _readerLoop = Task.CompletedTask;

    private IDatabase Redis => redis.Connection.GetDatabase();

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("streamer");
        await _db.Database.MigrateAsync();
        _connectionString = sql.ConnectionStringFor("streamer");
        var now = DateTime.UtcNow;
        for (var i = 0; i < _bidders.Length; i++)
        {
            _db.Users.Add(new User
            {
                UserId = _bidders[i], Username = $"bidder{i}xx", Email = $"b{i}@t.local", PasswordHash = "x",
                Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now,
            });
        }

        await _db.SaveChangesAsync();

        _bus = new AuctionEventBus();
        _handles = HandleCacheFactory.Create(redis.Connection, _connectionString);
        var reader = new PartitionStreamReader(redis.Connection, _bus, _handles, TimeProvider.System,
            NullLogger<PartitionStreamReader>.Instance, partition: 0);
        await reader.StartAsync();
        _readerLoop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                if (await reader.ReadOnceAsync() == 0)
                {
                    await Task.Delay(10);
                }
            }
        });
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        await _readerLoop;
        await _db.DisposeAsync();
    }

    private AuctionStreamer Streamer(TimeSpan? heartbeat = null) => new(
        _bus,
        new SnapshotBuilder(redis.Connection, RedisOptions.Default, _db, _handles, TimeProvider.System, NullLogger<SnapshotBuilder>.Instance),
        _handles,
        redis.Connection,
        RedisOptions.Default,
        new LiveOptions { HeartbeatEvery = heartbeat ?? TimeSpan.FromSeconds(15) },
        TimeProvider.System,
        NullLogger<AuctionStreamer>.Instance);

    private async Task<AuctionKeys> LiveAuctionAsync() =>
        await LiveAuctionSeeder.SeedAsync(Redis, new(Guid.NewGuid(), Guid.NewGuid(), StartC: 100, Inc: "0:100"));

    /// <summary>Each bid beats the last by 10.00, so every bid is a new public event.</summary>
    private async Task BidAsync(AuctionKeys keys, int bidder, long maxC)
    {
        var user = _bidders[bidder];
        var outcome = await new BidScript(new ScriptRegistry(redis.Connection))
            .RunAsync(keys, user, maxC, BidAmountRules.Fingerprint(user, maxC, "USD"), Guid.NewGuid(), "USD", "t");
        outcome.Accepted.Should().BeTrue();
    }

    /// <summary>Starts a subscriber in the background; its messages land in the returned channel.</summary>
    private (Channel<ProtoAuctionEvent> Out, Task Run) Subscribe(AuctionKeys keys, long lastSeen, TimeSpan? heartbeat = null)
    {
        var output = Channel.CreateUnbounded<ProtoAuctionEvent>();
        var run = Task.Run(() => Streamer(heartbeat).RunAsync(keys.AuctionId, lastSeen, e => output.Writer.WriteAsync(e).AsTask(), _stop.Token));
        return (output, run);
    }

    private static async Task<ProtoAuctionEvent> NextAsync(Channel<ProtoAuctionEvent> output, int waitMs = 3000)
    {
        using var timeout = new CancellationTokenSource(waitMs);
        return await output.Reader.ReadAsync(timeout.Token);
    }

    [Fact]
    public async Task Last_seen_0_gets_a_snapshot_then_the_caught_up_heartbeat_then_live()
    {
        var keys = await LiveAuctionAsync();
        await BidAsync(keys, 0, 1000);
        await BidAsync(keys, 1, 2000);

        var (output, _) = Subscribe(keys, lastSeen: 0);

        var first = await NextAsync(output);
        first.Snapshot.Should().NotBeNull();
        first.Seq.Should().Be(2);
        (await NextAsync(output)).Heartbeat.Should().NotBeNull();

        await BidAsync(keys, 0, 3000);
        var live = await NextAsync(output);
        live.Seq.Should().Be(3);
        live.BidAccepted.Should().NotBeNull();
    }

    [Fact]
    public async Task Last_seen_n_gets_exactly_the_missed_events_then_live()
    {
        var keys = await LiveAuctionAsync();
        for (var i = 0; i < 5; i++)
        {
            await BidAsync(keys, i % 2, 1000 + (i * 1000));   // seq 1..5
        }

        var (output, _) = Subscribe(keys, lastSeen: 2);

        (await NextAsync(output)).Seq.Should().Be(3);
        (await NextAsync(output)).Seq.Should().Be(4);
        (await NextAsync(output)).Seq.Should().Be(5);
        var mark = await NextAsync(output);
        mark.Heartbeat.Should().NotBeNull();
        mark.Seq.Should().Be(5);

        await BidAsync(keys, 1, 9000);   // bidder 0 leads; bidder 1 must bid, or it is only a hidden max raise
        (await NextAsync(output)).Seq.Should().Be(6);
    }

    [Fact]
    public async Task Last_seen_older_than_the_kept_events_gets_a_snapshot()
    {
        var keys = await LiveAuctionAsync();
        for (var i = 0; i < 4; i++)
        {
            await BidAsync(keys, i % 2, 1000 + (i * 1000));
        }

        await Redis.SortedSetRemoveRangeByScoreAsync(keys.Events, 0, 2);   // as if 1 and 2 were trimmed

        var (output, _) = Subscribe(keys, lastSeen: 1);

        var first = await NextAsync(output);
        first.Snapshot.Should().NotBeNull();
        first.Seq.Should().Be(4);
    }

    [Fact]
    public async Task Up_to_date_client_gets_just_the_heartbeat()
    {
        var keys = await LiveAuctionAsync();
        await BidAsync(keys, 0, 1000);

        var (output, _) = Subscribe(keys, lastSeen: 1);

        var first = await NextAsync(output);
        first.Heartbeat.Should().NotBeNull();
        first.Seq.Should().Be(1);
    }

    [Fact]
    public async Task Quiet_stream_sends_heartbeats()
    {
        var keys = await LiveAuctionAsync();

        var (output, _) = Subscribe(keys, lastSeen: 0, heartbeat: TimeSpan.FromMilliseconds(300));
        (await NextAsync(output)).Snapshot.Should().NotBeNull();
        (await NextAsync(output)).Heartbeat.Should().NotBeNull();   // caught up

        var beat = await NextAsync(output, 2000);
        beat.Heartbeat.Should().NotBeNull();
        beat.ServerTsMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Bids_during_catch_up_give_no_gap_and_no_repeat()
    {
        var keys = await LiveAuctionAsync();
        for (var i = 0; i < 20; i++)
        {
            await BidAsync(keys, i % 6, 1000 + (i * 1000));   // seq 1..20
        }

        // Keep bidding while the subscriber catches up from seq 5.
        var bidding = Task.Run(async () =>
        {
            for (var i = 20; i < 60; i++)
            {
                await BidAsync(keys, i % 6, 1000 + (i * 1000));
            }
        });
        var (output, _) = Subscribe(keys, lastSeen: 5);
        await bidding;

        // Collect until seq 60 arrives.
        var seqs = new List<long>();
        var snapshots = 0;
        while (seqs.Count == 0 || seqs[^1] < 60)
        {
            var e = await NextAsync(output, 5000);
            if (e.Heartbeat != null)
            {
                continue;
            }

            if (e.Snapshot != null)
            {
                snapshots++;   // a resync is allowed; it restarts the count from its seq
                seqs.Add(e.Seq);
                continue;
            }

            seqs.Add(e.Seq);
        }

        snapshots.Should().Be(0, "the catch-up should not need a resync");
        seqs.Should().Equal(Enumerable.Range(6, 55).Select(x => (long)x), "every seq from 6 to 60, each once, in order");
    }
}
