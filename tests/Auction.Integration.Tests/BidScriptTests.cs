using System.Text.Json;
using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 03 task 4: bid.lua on a real Redis.
/// Every vector in tests/vectors/proxy.json must give the same answer as ProxyResolver (R4.7).
/// Each test uses its own auction id, so tests do not see each other's keys.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class BidScriptTests(RedisFixture redis)
{
    private static readonly Guid Seller = Guid.Parse("55555555-0000-0000-0000-000000000005");

    private static readonly Dictionary<string, Guid> Users = new()
    {
        ["A"] = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        ["B"] = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
        ["C"] = Guid.Parse("cccccccc-0000-0000-0000-000000000003"),
    };

    private IDatabase Db => redis.Connection.GetDatabase();

    private BidScript Script() => new(new ScriptRegistry(redis.Connection));

    private Task<BidOutcome> BidAsync(AuctionKeys keys, Guid user, long maxC, Guid? idem = null, string currency = "USD") =>
        Script().RunAsync(keys, user, maxC, BidAmountRules.Fingerprint(user, maxC, currency), idem ?? Guid.NewGuid(), currency, "test");

    // ---------- the shared vectors ----------

    public sealed record VState(long PriceC, string? Leader, long LeaderMaxC, int BidCount, long StartC);

    public sealed record VExpect(string? Error);

    public sealed record Vector(string Name, string? Inc, VState State, string User, long MaxC, VExpect Expect)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<Vector> Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "proxy.json");
        var list = JsonSerializer.Deserialize<List<Vector>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var data = new TheoryData<Vector>();
        foreach (var v in list)
        {
            data.Add(v);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task Lua_matches_ProxyResolver(Vector vector)
    {
        var inc = vector.Inc ?? LiveAuctionSeeder.Table1;
        var leader = vector.State.Leader == null ? (Guid?)null : Users[vector.State.Leader];
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new LiveAuctionSeeder.Setup(Guid.NewGuid(), Seller,
            StartC: vector.State.StartC, PriceC: vector.State.PriceC, Leader: leader, LeaderMaxC: vector.State.LeaderMaxC,
            BidCount: vector.State.BidCount, Inc: inc));
        var before = new BidState(vector.State.PriceC, leader, vector.State.LeaderMaxC, vector.State.BidCount, vector.State.StartC);
        var user = Users[vector.User];

        var outcome = await BidAsync(keys, user, vector.MaxC);

        Resolution? expected = null;
        DomainException? expectedError = null;
        try
        {
            expected = ProxyResolver.Resolve(before, user, vector.MaxC, IncrementTable.Parse(inc));
        }
        catch (DomainException ex)
        {
            expectedError = ex;
        }

        if (expectedError != null)
        {
            outcome.Accepted.Should().BeFalse();
            outcome.ErrorCode.Should().Be(expectedError.Code);
            outcome.MinNextC.Should().Be(expectedError.MinNextBidC);
            (await LiveAuctionSeeder.ReadBidStateAsync(Db, keys)).Should().Be(before, "a rejected bid changes nothing");
        }
        else
        {
            outcome.Accepted.Should().BeTrue();
            outcome.Result.Should().Be(expected!.Result);
            outcome.PriceC.Should().Be(expected.Next.PriceC);
            outcome.BidCount.Should().Be(expected.Next.BidCount);
            (await LiveAuctionSeeder.ReadBidStateAsync(Db, keys)).Should().Be(expected.Next);

            var entry = (await Db.StreamRangeAsync(keys.Stream, messageOrder: Order.Descending, count: 50))
                .First(e => e["auction"] == keys.AuctionId.ToString());
            var rows = JsonSerializer.Deserialize<List<StreamRow>>(entry["rows"].ToString())!
                .Select(r => new BidRow(r.User, r.AmountC, Enum.Parse<BidKind>(r.Kind)));
            rows.Should().Equal(expected.Rows, "the stream carries the same rows ProxyResolver made");
        }

        await LiveAuctionSeeder.CleanUpAsync(Db, keys);
    }

    private sealed record StreamRow(
        [property: System.Text.Json.Serialization.JsonPropertyName("user")] Guid User,
        [property: System.Text.Json.Serialization.JsonPropertyName("amountC")] long AmountC,
        [property: System.Text.Json.Serialization.JsonPropertyName("kind")] string Kind);

    // ---------- auction must be open (R2) ----------

    [Theory]
    [InlineData("Scheduled", "NOT_LIVE")]
    [InlineData("Sealed", "NOT_LIVE")]
    [InlineData("Settled", "NOT_LIVE")]
    [InlineData("Frozen", "AUCTION_FROZEN")]
    public async Task Closed_statuses_are_refused(string status, string code)
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller, Status: status));

        (await BidAsync(keys, Users["A"], 5000)).ErrorCode.Should().Be(code);
    }

    [Fact]
    public async Task Live_but_not_started_by_redis_time_is_NOT_LIVE()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller, StartsInMs: 60_000));

        (await BidAsync(keys, Users["A"], 5000)).ErrorCode.Should().Be(ErrorCodes.NotLive);
    }

    [Fact]
    public async Task Past_the_end_is_ENDED()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller, EndsInMs: -1));

        (await BidAsync(keys, Users["A"], 5000)).ErrorCode.Should().Be(ErrorCodes.Ended);
    }

    [Fact]
    public async Task Seller_gets_OWN_LOT()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller));

        (await BidAsync(keys, Seller, 5000)).ErrorCode.Should().Be(ErrorCodes.OwnLot);
    }

    [Fact]
    public async Task Missing_state_is_reported_and_not_stored()
    {
        var keys = RedisKeys.For(Guid.NewGuid(), 1);
        var idem = Guid.NewGuid();

        (await BidAsync(keys, Users["A"], 5000, idem)).ErrorCode.Should().Be(BidOutcome.Missing);
        (await Db.KeyExistsAsync(keys.Idempotency(idem.ToString()))).Should().BeFalse();
    }

    [Fact]
    public async Task Wrong_currency_is_reported_and_not_stored()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller));
        var idem = Guid.NewGuid();

        (await BidAsync(keys, Users["A"], 5000, idem, currency: "EUR")).ErrorCode.Should().Be(BidOutcome.CurrencyMismatch);
        (await Db.KeyExistsAsync(keys.Idempotency(idem.ToString()))).Should().BeFalse();
    }

    // ---------- anti-sniping (R5) ----------

    [Fact]
    public async Task Bid_in_the_snipe_window_extends_the_end()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller,
            EndsInMs: 30_000, SnipeMs: 60_000, ExtMs: 60_000, MaxExtMs: 3_600_000));
        var endBefore = (long)await Db.HashGetAsync(keys.State, "endMs");

        var outcome = await BidAsync(keys, Users["A"], 5000);

        outcome.Extended.Should().BeTrue();
        outcome.EndMs.Should().Be(endBefore + 60_000);
        ((long)await Db.HashGetAsync(keys.State, "extUsedMs")).Should().Be(60_000);
    }

    [Fact]
    public async Task Extension_is_capped_by_the_max_total()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller,
            EndsInMs: 30_000, SnipeMs: 60_000, ExtMs: 60_000, MaxExtMs: 90_000, ExtUsedMs: 70_000));
        var endBefore = (long)await Db.HashGetAsync(keys.State, "endMs");

        var outcome = await BidAsync(keys, Users["A"], 5000);

        outcome.EndMs.Should().Be(endBefore + 20_000, "only 20 s of the 90 s cap was left");
        ((long)await Db.HashGetAsync(keys.State, "extUsedMs")).Should().Be(90_000);

        var second = await BidAsync(keys, Users["B"], 9000);
        second.Extended.Should().BeFalse("the cap is used up");
    }

    [Fact]
    public async Task Bid_outside_the_window_does_not_extend()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller,
            EndsInMs: 600_000, SnipeMs: 60_000, ExtMs: 60_000, MaxExtMs: 3_600_000));

        (await BidAsync(keys, Users["A"], 5000)).Extended.Should().BeFalse();
    }

    [Fact]
    public async Task Max_raise_never_extends()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller,
            PriceC: 1000, Leader: Users["A"], LeaderMaxC: 5000, BidCount: 1,
            EndsInMs: 30_000, SnipeMs: 60_000, ExtMs: 60_000, MaxExtMs: 3_600_000));

        var outcome = await BidAsync(keys, Users["A"], 8000);

        outcome.Result.Should().Be(BidResult.MaxRaised);
        outcome.Extended.Should().BeFalse();
        outcome.Seq.Should().Be(0, "a max raise is not a public event");
    }

    // ---------- idempotency (R6) ----------

    [Fact]
    public async Task Same_key_same_bid_returns_the_first_outcome_without_bidding_again()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller));
        var idem = Guid.NewGuid();

        var first = await BidAsync(keys, Users["A"], 5000, idem);
        var again = await BidAsync(keys, Users["A"], 5000, idem);

        again.Should().Be(first with { Replayed = true });
        ((long)await Db.HashGetAsync(keys.State, "eseq")).Should().Be(1, "the second call did not run the bid");
        (await Db.KeyTimeToLiveAsync(keys.Idempotency(idem.ToString()))).Should().BeCloseTo(TimeSpan.FromHours(24), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Same_key_replays_a_rejection_too()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller));
        var idem = Guid.NewGuid();

        var first = await BidAsync(keys, Users["A"], 500, idem);    // below the 10.00 start
        var again = await BidAsync(keys, Users["A"], 500, idem);

        first.ErrorCode.Should().Be(ErrorCodes.BelowMinimum);
        again.Should().Be(first with { Replayed = true });
    }

    [Fact]
    public async Task Same_key_different_amount_is_IDEMPOTENCY_MISMATCH()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller));
        var idem = Guid.NewGuid();
        await BidAsync(keys, Users["A"], 5000, idem);

        (await BidAsync(keys, Users["A"], 6000, idem)).ErrorCode.Should().Be(ErrorCodes.IdempotencyMismatch);
        (await BidAsync(keys, Users["B"], 5000, idem)).ErrorCode.Should().Be(ErrorCodes.IdempotencyMismatch);
    }

    // ---------- records ----------

    [Fact]
    public async Task Public_rows_and_events_have_no_max_and_seq_counts_price_changes()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller));

        await BidAsync(keys, Users["A"], 50_000);   // A leads at 10.00
        await BidAsync(keys, Users["B"], 2_000);    // A's proxy answers
        await BidAsync(keys, Users["A"], 70_000);   // max raise: no seq

        ((long)await Db.HashGetAsync(keys.State, "seq")).Should().Be(2);
        ((long)await Db.HashGetAsync(keys.State, "eseq")).Should().Be(3);

        var rows = await Db.SortedSetRangeByRankAsync(keys.Bids);
        var events = await Db.SortedSetRangeByRankAsync(keys.Events);
        rows.Should().HaveCount(3, "1 row for the first bid, 2 for the proxy answer, none for the raise");
        events.Should().HaveCount(2);
        rows.Concat(events).Select(r => r.ToString()).Should().NotContain(s => s.Contains("50000") || s.Contains("70000") || s.Contains("max"));

        ((long)await Db.HashGetAsync(keys.Max, Users["A"].ToString())).Should().Be(70_000);
        ((long)await Db.HashGetAsync(keys.Max, Users["B"].ToString())).Should().Be(2_000);
    }

    [Fact]
    public async Task Script_reloads_after_redis_forgets_it()
    {
        var keys = await LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Seller));
        var script = Script();
        await BidAsync(keys, Users["A"], 5000);

        foreach (var endpoint in redis.Connection.GetEndPoints())
        {
            await redis.Connection.GetServer(endpoint).ScriptFlushAsync();
        }

        var outcome = await script.RunAsync(keys, Users["B"], 9000, BidAmountRules.Fingerprint(Users["B"], 9000, "USD"), Guid.NewGuid(), "USD", "t");

        outcome.Accepted.Should().BeTrue("NOSCRIPT made the registry load the script again");
    }
}
