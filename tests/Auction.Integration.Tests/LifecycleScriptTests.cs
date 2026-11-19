using Auction.Domain.Bidding;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 05 task 2: the lifecycle Lua scripts on a real Redis, in their own partition {p960}.
/// Each guard, idempotency, re-scoring, seq/eseq +1 per event, and the events ZSET and stream entry.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class LifecycleScriptTests(RedisFixture redis)
{
    private const int Partition = 960;
    private static readonly PartitionKeys Index = new(Partition);

    private IDatabase Db => redis.Connection.GetDatabase();

    private LifecycleScripts Scripts => new(new ScriptRegistry(redis.Connection));

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static AuctionLoad Load(long startMs, long endMs) =>
        new(Guid.NewGuid(), startMs, endMs, StartC: 1000, ReserveC: null, SnipeMs: 0, ExtMs: 0, MaxExtMs: 0,
            LiveAuctionSeeder.Table1, "USD");

    private Task<AuctionKeys> LiveAsync(long endsInMs = 3_600_000, int bidCount = 0) =>
        LiveAuctionSeeder.SeedAsync(Db, new(Guid.NewGuid(), Guid.NewGuid(), EndsInMs: endsInMs, BidCount: bidCount), Partition);

    private static AuctionKeys NewKeys() => new(Guid.NewGuid(), Partition);

    private async Task<string?> StatusAsync(AuctionKeys keys) => await Db.HashGetAsync(keys.State, "status");

    private async Task<long> HashLongAsync(AuctionKeys keys, string field) => (long)await Db.HashGetAsync(keys.State, field);

    /// <summary>This auction's stream entries, oldest first, read back with the persister's parser.</summary>
    private async Task<List<PersistEntry>> EntriesAsync(AuctionKeys keys) =>
        (await Db.StreamRangeAsync(keys.Stream))
            .Where(e => e["auction"] == keys.AuctionId.ToString())
            .Select(e => PersistEntryParser.Parse(e.Id!, e.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString())))
            .ToList();

    private async Task<string[]> EventsAsync(AuctionKeys keys) =>
        (await Db.SortedSetRangeByRankAsync(keys.Events)).Select(v => v.ToString()).ToArray();

    // ---------- load ----------

    [Fact]
    public async Task Load_creates_a_scheduled_state_and_indexes_the_start()
    {
        var keys = NewKeys();
        var start = Now + 120_000;

        (await Scripts.LoadAsync(keys, Load(start, start + 600_000))).Should().Be(LifecycleResult.Done);

        (await StatusAsync(keys)).Should().Be("Scheduled");
        (await HashLongAsync(keys, "seq")).Should().Be(0);
        (await Db.SortedSetScoreAsync(Index.Starts, keys.AuctionId.ToString())).Should().Be(start);
        (await EntriesAsync(keys)).Should().BeEmpty("loading emits no event");
    }

    [Fact]
    public async Task Second_load_never_resets_a_running_auction()
    {
        var keys = await LiveAsync();
        await Db.HashSetAsync(keys.State, "priceC", 5000);

        (await Scripts.LoadAsync(keys, Load(Now, Now + 600_000))).Should().Be(LifecycleResult.Exists);

        (await StatusAsync(keys)).Should().Be("Live");
        (await HashLongAsync(keys, "priceC")).Should().Be(5000);
    }

    // ---------- open ----------

    [Fact]
    public async Task Open_before_the_start_is_not_due()
    {
        var keys = NewKeys();
        await Scripts.LoadAsync(keys, Load(Now + 60_000, Now + 600_000));

        (await Scripts.OpenAsync(keys)).Should().Be(LifecycleResult.NotDue);

        (await StatusAsync(keys)).Should().Be("Scheduled");
    }

    [Fact]
    public async Task Open_twice_gives_one_event()
    {
        var keys = NewKeys();
        var end = Now + 600_000;
        await Scripts.LoadAsync(keys, Load(Now - 1000, end));

        (await Scripts.OpenAsync(keys)).Should().Be(LifecycleResult.Done);
        (await Scripts.OpenAsync(keys)).Should().Be(LifecycleResult.Skipped);

        (await StatusAsync(keys)).Should().Be("Live");
        var entries = await EntriesAsync(keys);
        entries.Should().ContainSingle();
        entries[0].Should().Match<PersistEntry>(e => e.Status == "Live" && e.Change == "Opened" && e.Seq == 1 && e.ESeq == 1);
        (await EventsAsync(keys)).Should().ContainSingle().Which.Should().Contain("\"change\":\"Opened\"");
        (await Db.SortedSetScoreAsync(Index.Starts, keys.AuctionId.ToString())).Should().BeNull();
        (await Db.SortedSetScoreAsync(Index.Ends, keys.AuctionId.ToString())).Should().Be(end);
        (await Db.SortedSetScoreAsync(Index.Soon, keys.AuctionId.ToString())).Should().Be(end - 60_000);
    }

    [Fact]
    public async Task Open_on_missing_state_says_missing_and_cleans_the_index()
    {
        var keys = NewKeys();
        await Db.SortedSetAddAsync(Index.Starts, keys.AuctionId.ToString(), Now);

        (await Scripts.OpenAsync(keys)).Should().Be(LifecycleResult.Missing);

        (await Db.SortedSetScoreAsync(Index.Starts, keys.AuctionId.ToString())).Should().BeNull();
    }

    // ---------- soon ----------

    [Fact]
    public async Task Closing_soon_is_sent_once()
    {
        var keys = await LiveAsync(endsInMs: 30_000);

        (await Scripts.SoonAsync(keys)).Should().Be(LifecycleResult.Done);
        (await Scripts.SoonAsync(keys)).Should().Be(LifecycleResult.Skipped);

        var entries = await EntriesAsync(keys);
        entries.Should().ContainSingle().Which.Change.Should().Be("ClosingSoon");
        entries[0].Status.Should().Be("Live", "bidding goes on");
    }

    [Fact]
    public async Task Closing_soon_after_the_end_moved_later_is_not_due_and_re_scored()
    {
        var keys = await LiveAsync(endsInMs: 600_000);
        var end = await HashLongAsync(keys, "endMs");

        (await Scripts.SoonAsync(keys)).Should().Be(LifecycleResult.NotDue);

        (await Db.SortedSetScoreAsync(Index.Soon, keys.AuctionId.ToString())).Should().Be(end - 60_000);
        (await EntriesAsync(keys)).Should().BeEmpty();
    }

    // ---------- seal ----------

    [Fact]
    public async Task Seal_before_the_end_is_not_due_and_re_scores_the_end()
    {
        var keys = await LiveAsync(endsInMs: 600_000);
        var end = await HashLongAsync(keys, "endMs");
        await Db.SortedSetAddAsync(Index.Ends, keys.AuctionId.ToString(), end - 500_000);   // stale, as after an extension

        (await Scripts.SealAsync(keys)).Should().Be(LifecycleResult.NotDue);

        (await StatusAsync(keys)).Should().Be("Live");
        (await Db.SortedSetScoreAsync(Index.Ends, keys.AuctionId.ToString())).Should().Be(end);
    }

    [Fact]
    public async Task Seal_at_the_end_seals_and_bids_then_get_not_live()
    {
        var keys = await LiveAsync(endsInMs: -1);

        (await Scripts.SealAsync(keys)).Should().Be(LifecycleResult.Done);

        (await StatusAsync(keys)).Should().Be("Sealed");
        (await Db.SortedSetScoreAsync(Index.Sealed, keys.AuctionId.ToString())).Should().NotBeNull();
        (await Db.SortedSetScoreAsync(Index.Ends, keys.AuctionId.ToString())).Should().BeNull();
        (await EntriesAsync(keys)).Should().ContainSingle().Which.Status.Should().Be("Sealed");

        var outcome = await BidAsync(keys, 5000);
        outcome.ErrorCode.Should().Be("NOT_LIVE");
    }

    // ---------- settle ----------

    [Fact]
    public async Task Settle_needs_sealed()
    {
        var keys = await LiveAsync();

        (await Scripts.SettleAsync(keys, "Unsold", 0, null)).Should().Be(LifecycleResult.Skipped);

        (await StatusAsync(keys)).Should().Be("Live");
    }

    [Fact]
    public async Task Settle_once_with_outcome_price_and_winner_then_keys_expire()
    {
        var keys = await LiveAsync(endsInMs: -1);
        await Scripts.SealAsync(keys);
        var winner = Guid.NewGuid();

        (await Scripts.SettleAsync(keys, "Sold", 12_500, winner)).Should().Be(LifecycleResult.Done);
        (await Scripts.SettleAsync(keys, "Sold", 12_500, winner)).Should().Be(LifecycleResult.Skipped);

        (await StatusAsync(keys)).Should().Be("Settled");
        var settled = (await EntriesAsync(keys)).Last();
        settled.Should().Match<PersistEntry>(e => e.Change == "Settled" && e.Outcome == "Sold" && e.FinalC == 12_500 && e.Winner == winner);
        (await Db.SortedSetScoreAsync(Index.Sealed, keys.AuctionId.ToString())).Should().BeNull();
        var ttl = await Db.KeyTimeToLiveAsync(keys.State);
        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(600));
    }

    // ---------- control ----------

    [Fact]
    public async Task Seller_cancel_with_a_bid_is_refused()
    {
        var keys = await LiveAsync();
        (await BidAsync(keys, 5000)).Accepted.Should().BeTrue();

        (await Scripts.ControlAsync(keys, ControlOp.Cancel)).Should().Be(LifecycleResult.InvalidState);

        (await StatusAsync(keys)).Should().Be("Live");
    }

    [Fact]
    public async Task Seller_cancel_with_no_bids_cancels()
    {
        var keys = await LiveAsync();

        (await Scripts.ControlAsync(keys, ControlOp.Cancel)).Should().Be(LifecycleResult.Done);

        (await StatusAsync(keys)).Should().Be("Cancelled");
        (await EntriesAsync(keys)).Should().ContainSingle().Which.Change.Should().Be("Cancelled");
        (await Db.SortedSetScoreAsync(Index.Ends, keys.AuctionId.ToString())).Should().BeNull();
    }

    [Fact]
    public async Task Admin_force_cancel_works_with_bids()
    {
        var keys = await LiveAsync();
        await BidAsync(keys, 5000);

        (await Scripts.ControlAsync(keys, ControlOp.Cancel, force: true)).Should().Be(LifecycleResult.Done);

        (await StatusAsync(keys)).Should().Be("Cancelled");
    }

    [Fact]
    public async Task Force_close_ends_now_and_seal_follows()
    {
        var keys = await LiveAsync();

        (await Scripts.ControlAsync(keys, ControlOp.Close)).Should().Be(LifecycleResult.Done);

        (await HashLongAsync(keys, "endMs")).Should().BeCloseTo(Now, 5_000);
        (await EntriesAsync(keys)).Should().BeEmpty("close itself sends no event");
        (await Scripts.SealAsync(keys)).Should().Be(LifecycleResult.Done);
    }

    [Fact]
    public async Task Freeze_blocks_bids_and_resume_pushes_the_end_later()
    {
        var keys = await LiveAsync(endsInMs: 600_000);
        var end = await HashLongAsync(keys, "endMs");

        (await Scripts.ControlAsync(keys, ControlOp.Freeze)).Should().Be(LifecycleResult.Done);
        (await BidAsync(keys, 5000)).ErrorCode.Should().Be("AUCTION_FROZEN");
        await Task.Delay(400);

        (await Scripts.ControlAsync(keys, ControlOp.Resume)).Should().Be(LifecycleResult.Done);

        (await StatusAsync(keys)).Should().Be("Live");
        (await HashLongAsync(keys, "endMs")).Should().BeGreaterThanOrEqualTo(end + 400);
        (await Db.HashExistsAsync(keys.State, "frozenAtMs")).Should().BeFalse();
        (await EntriesAsync(keys)).Select(e => e.Change).Should().Equal("Frozen", "Resumed");
    }

    [Fact]
    public async Task Resume_leaves_at_least_60_seconds()
    {
        var keys = await LiveAsync(endsInMs: 5_000);
        await Scripts.ControlAsync(keys, ControlOp.Freeze);

        await Scripts.ControlAsync(keys, ControlOp.Resume);

        (await HashLongAsync(keys, "endMs")).Should().BeGreaterThanOrEqualTo(Now + 55_000);
    }

    [Theory]
    [InlineData(ControlOp.Close)]
    [InlineData(ControlOp.Freeze)]
    [InlineData(ControlOp.Resume)]
    public async Task Control_ops_refuse_the_wrong_status(ControlOp op)
    {
        var keys = await LiveAsync(endsInMs: -1);
        await Scripts.SealAsync(keys);

        (await Scripts.ControlAsync(keys, op)).Should().Be(LifecycleResult.InvalidState);
    }

    // ---------- rebuild ----------

    [Fact]
    public async Task Rebuild_makes_a_frozen_state_with_the_sql_values()
    {
        var keys = NewKeys();
        var leader = Guid.NewGuid();
        var other = Guid.NewGuid();
        var load = Load(Now - 60_000, Now + 600_000);
        var rebuild = new AuctionRebuild(load, load.EndMs, ExtUsedMs: 0, PriceC: 2500, leader, LeaderMaxC: 9000, BidCount: 3,
            Seq: 3, ESeq: 4, new Dictionary<Guid, long> { [leader] = 9000, [other] = 2000 });

        (await Scripts.RebuildAsync(keys, rebuild)).Should().Be(LifecycleResult.Done);
        (await Scripts.RebuildAsync(keys, rebuild)).Should().Be(LifecycleResult.Exists);

        (await StatusAsync(keys)).Should().Be("Frozen");
        (await HashLongAsync(keys, "priceC")).Should().Be(2500);
        ((string?)await Db.HashGetAsync(keys.State, "leader")).Should().Be(leader.ToString());
        (await HashLongAsync(keys, "persistedEseq")).Should().Be(4);
        (await HashLongAsync(keys, "seq")).Should().Be(4, "the Frozen event takes seq 4");
        (await HashLongAsync(keys, "eseq")).Should().Be(5);
        ((long)await Db.HashGetAsync(keys.Max, other.ToString())).Should().Be(2000);
    }

    // ---------- shared emit ----------

    [Fact]
    public async Task Every_event_adds_one_to_seq_and_eseq()
    {
        var keys = NewKeys();
        await Scripts.LoadAsync(keys, Load(Now - 1000, Now + 30_000));

        await Scripts.OpenAsync(keys);
        await Scripts.SoonAsync(keys);
        await Scripts.ControlAsync(keys, ControlOp.Close);
        await Scripts.SealAsync(keys);

        var entries = await EntriesAsync(keys);
        entries.Select(e => e.Change).Should().Equal("Opened", "ClosingSoon", "Sealed");
        entries.Select(e => e.Seq).Should().Equal(1L, 2L, 3L);
        entries.Select(e => e.ESeq).Should().Equal(1L, 2L, 3L);
        (await EventsAsync(keys)).Should().HaveCount(3);
    }

    [Fact]
    public async Task State_events_carry_no_private_values()
    {
        var keys = await LiveAsync(endsInMs: -1);
        await Db.HashSetAsync(keys.State, [new("reserveC", 99_999), new("leaderMaxC", 88_888)]);
        await Scripts.SealAsync(keys);
        await Scripts.SettleAsync(keys, "Unsold", 0, null);

        var raw = string.Join("|", (await Db.StreamRangeAsync(keys.Stream))
            .Where(e => e["auction"] == keys.AuctionId.ToString())
            .SelectMany(e => e.Values.Select(v => $"{v.Name}={v.Value}")));

        raw.Should().NotContain("99999").And.NotContain("88888").And.NotContain("maxC");
        string.Join("|", await EventsAsync(keys)).Should().NotContain("99999").And.NotContain("88888");
    }

    private Task<BidOutcome> BidAsync(AuctionKeys keys, long maxC)
    {
        var user = Guid.NewGuid();
        return new BidScript(new ScriptRegistry(redis.Connection))
            .RunAsync(keys, user, maxC, BidAmountRules.Fingerprint(user, maxC, "USD"), Guid.NewGuid(), "USD", "t");
    }
}
