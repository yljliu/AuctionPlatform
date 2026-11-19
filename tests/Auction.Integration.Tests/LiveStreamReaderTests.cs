using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Live;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;
using ProtoAuctionEvent = Auction.Contracts.AuctionEvent;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 04 task 2: PartitionStreamReader on a real Redis, publishing to the bus.
/// Each test uses its own partition ({p95x}) so streams do not mix.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class LiveStreamReaderTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
{
    private AuctionDbContext _db = null!;
    private string _connectionString = "";
    private Guid _alice, _bob;

    private IDatabase Redis => redis.Connection.GetDatabase();

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("live_reader");
        await _db.Database.MigrateAsync();
        _connectionString = sql.ConnectionStringFor("live_reader");

        var now = DateTime.UtcNow;
        var alice = new User { UserId = Guid.NewGuid(), Username = "alice42x", Email = "a@t.local", PasswordHash = "x", Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now };
        var bob = new User { UserId = Guid.NewGuid(), Username = "bobsmith42", Email = "b@t.local", PasswordHash = "x", Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now };
        _db.Users.AddRange(alice, bob);
        await _db.SaveChangesAsync();
        (_alice, _bob) = (alice.UserId, bob.UserId);
        await Redis.KeyDeleteAsync([RedisKeys.Handle(_alice), RedisKeys.Handle(_bob)]);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private PartitionStreamReader Reader(AuctionEventBus bus, int partition) =>
        new(redis.Connection, bus, HandleCacheFactory.Create(redis.Connection, _connectionString), TimeProvider.System,
            NullLogger<PartitionStreamReader>.Instance, partition);

    private async Task<string> FreshStreamAsync(int partition)
    {
        var stream = $"{{p{partition}}}:stream";
        await Redis.KeyDeleteAsync(stream);
        return stream;
    }

    private NameValueEntry[] FakeBid(Guid auction, string seq, string result, string rowsKind = "Manual") =>
    [
        new("type", "Bid"), new("auction", auction.ToString()), new("eseq", "1"), new("seq", seq), new("user", _alice.ToString()),
        new("maxC", "7777700"), new("result", result), new("priceC", "1000"), new("leader", _alice.ToString()),
        new("leaderMaxC", "7777700"), new("bidCount", "1"), new("endMs", "1"), new("extended", "0"), new("reserveMet", ""),
        new("minNextC", "1100"), new("cur", "USD"),
        new("rows", $"[{{\"seq\":1,\"user\":\"{_alice}\",\"amountC\":1000,\"atMs\":1,\"kind\":\"{rowsKind}\"}}]"),
        new("outbid", ""), new("idem", Guid.NewGuid().ToString()), new("at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()), new("corr", "t"),
    ];

    private static async Task<ProtoAuctionEvent?> NextAsync(AuctionSubscription sub, int waitMs = 2000)
    {
        using var timeout = new CancellationTokenSource(waitMs);
        try
        {
            return await sub.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Added_entry_reaches_the_subscriber()
    {
        var stream = await FreshStreamAsync(951);
        var bus = new AuctionEventBus();
        var auction = Guid.NewGuid();
        using var sub = bus.Subscribe(auction);
        var reader = Reader(bus, 951);
        await reader.StartAsync();

        await Redis.StreamAddAsync(stream, FakeBid(auction, "1", "Leading"));
        (await reader.ReadOnceAsync()).Should().Be(1);

        var live = await NextAsync(sub);
        live.Should().NotBeNull();
        live!.Seq.Should().Be(1);
        live.BidAccepted.Price.Units.Should().Be(10);
        live.BidAccepted.Leader.Handle.Should().Be("a***2x", "the name comes from SQL and is masked");
        JsonFormatter.Default.Format(live).Should().NotContain("\"units\": \"77777\"", "the max never leaves");
    }

    [Fact]
    public async Task Max_raise_entry_is_not_published()
    {
        var stream = await FreshStreamAsync(952);
        var bus = new AuctionEventBus();
        var auction = Guid.NewGuid();
        using var sub = bus.Subscribe(auction);
        var reader = Reader(bus, 952);
        await reader.StartAsync();

        await Redis.StreamAddAsync(stream, FakeBid(auction, "-", "MaxRaised", "MaxRaise"));
        (await reader.ReadOnceAsync()).Should().Be(1, "it was read");

        (await NextAsync(sub, 300)).Should().BeNull("but not published");
    }

    [Fact]
    public async Task Reader_starts_at_the_end_and_skips_older_entries()
    {
        var stream = await FreshStreamAsync(953);
        var bus = new AuctionEventBus();
        var auction = Guid.NewGuid();
        using var sub = bus.Subscribe(auction);
        await Redis.StreamAddAsync(stream, FakeBid(auction, "1", "Leading"));   // before the reader starts

        var reader = Reader(bus, 953);
        await reader.StartAsync();
        await Redis.StreamAddAsync(stream, FakeBid(auction, "2", "Leading"));
        await reader.ReadOnceAsync();

        (await NextAsync(sub))!.Seq.Should().Be(2);
        (await NextAsync(sub, 300)).Should().BeNull();
    }

    [Fact]
    public async Task Real_bid_from_bid_lua_reaches_the_subscriber_with_masked_names()
    {
        await FreshStreamAsync(954);
        var bus = new AuctionEventBus();
        var keys = await LiveAuctionSeeder.SeedAsync(Redis, new(Guid.NewGuid(), Guid.NewGuid()), partition: 954);
        using var sub = bus.Subscribe(keys.AuctionId);
        var reader = Reader(bus, 954);
        await reader.StartAsync();
        var script = new BidScript(new ScriptRegistry(redis.Connection));

        await script.RunAsync(keys, _alice, 500000, BidAmountRules.Fingerprint(_alice, 500000, "USD"), Guid.NewGuid(), "USD", "t");
        await script.RunAsync(keys, _bob, 300000, BidAmountRules.Fingerprint(_bob, 300000, "USD"), Guid.NewGuid(), "USD", "t");
        await reader.ReadOnceAsync();

        var first = await NextAsync(sub);
        var second = await NextAsync(sub);
        first!.Seq.Should().Be(1);
        second!.Seq.Should().Be(2);
        second.BidAccepted.Rows.Select(r => r.Bidder.Handle).Should().Equal("b***42", "a***2x");
        second.BidAccepted.Price.Units.Should().Be(3010, "Alice's proxy answers 3000.00 + 10.00 (the 1000.00 tier)");
        JsonFormatter.Default.Format(second).Should().NotContain("\"units\": \"5000\"", "Alice's max of 5000.00 stays private");

        (await Redis.StringGetAsync(RedisKeys.Handle(_alice))).ToString().Should().Be("alice42x", "the handle cache filled Redis");
    }

    [Fact]
    public async Task Entries_of_other_auctions_go_to_their_own_subscribers()
    {
        var stream = await FreshStreamAsync(955);
        var bus = new AuctionEventBus();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using var subA = bus.Subscribe(a);
        using var subB = bus.Subscribe(b);
        var reader = Reader(bus, 955);
        await reader.StartAsync();

        await Redis.StreamAddAsync(stream, FakeBid(a, "1", "Leading"));
        await Redis.StreamAddAsync(stream, FakeBid(b, "1", "Leading"));
        await reader.ReadOnceAsync();

        (await NextAsync(subA))!.AuctionId.Should().Be(a.ToString());
        (await NextAsync(subB))!.AuctionId.Should().Be(b.ToString());
        (await NextAsync(subA, 200)).Should().BeNull();
    }
}
