using System.Text.Json;
using System.Text.Json.Serialization;
using Auction.Domain.Bidding;
using Auction.Domain.Errors;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 03 task 5: 50 bidders send 20 bids each, all at once, to one auction (R7).
/// Then the stream is replayed through ProxyResolver, one entry at a time, and must end
/// in exactly the state Redis has.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class BidConcurrencyTests(RedisFixture redis)
{
    private const int Bidders = 50;
    private const int BidsEach = 20;

    private sealed record Entry(long Eseq, string Seq, Guid User, long MaxC, string Result, long PriceC, Guid Leader, long LeaderMaxC);

    [Fact]
    public async Task Many_parallel_bids_keep_seq_gapless_and_replay_to_the_same_state()
    {
        var db = redis.Connection.GetDatabase();
        var seller = Guid.NewGuid();
        var keys = await LiveAuctionSeeder.SeedAsync(db, new(Guid.NewGuid(), seller, StartC: 100));
        var script = new BidScript(new ScriptRegistry(redis.Connection));

        var people = Enumerable.Range(0, Bidders).Select(_ => Guid.NewGuid()).ToArray();
        var accepted = 0;
        var rejected = 0;

        // Every bidder bids in its own task; amounts climb and overlap so many bids race.
        await Task.WhenAll(people.Select((user, i) => Task.Run(async () =>
        {
            var random = new Random(i);
            for (var b = 0; b < BidsEach; b++)
            {
                var maxC = 100 + random.Next(0, 500) + (b * 400);
                var outcome = await script.RunAsync(keys, user, maxC, BidAmountRules.Fingerprint(user, maxC, "USD"), Guid.NewGuid(), "USD", "load");
                if (outcome.Accepted)
                {
                    Interlocked.Increment(ref accepted);
                }
                else
                {
                    outcome.ErrorCode.Should().Be(ErrorCodes.BelowMinimum, "the only allowed rejection here");
                    Interlocked.Increment(ref rejected);
                }
            }
        })));

        (accepted + rejected).Should().Be(Bidders * BidsEach);
        // How many win the race changes from run to run (46 to 60+ seen). The real checks are the
        // gapless seq and the replay below; this only proves bids were not all rejected.
        accepted.Should().BeGreaterThan(BidsEach, "plenty of bids got through");

        // Read back this auction's stream entries, in order.
        var entries = (await db.StreamRangeAsync(keys.Stream))
            .Where(e => e["auction"] == keys.AuctionId.ToString())
            .Select(e => new Entry(
                (long)e["eseq"], e["seq"].ToString(), Guid.Parse(e["user"].ToString()), (long)e["maxC"], e["result"].ToString(),
                (long)e["priceC"], Guid.Parse(e["leader"].ToString()), (long)e["leaderMaxC"]))
            .ToList();

        entries.Should().HaveCount(accepted, "one stream entry per accepted bid");

        // eseq goes 1, 2, 3, ... with no gaps or repeats.
        entries.Select(e => e.Eseq).Should().Equal(Enumerable.Range(1, accepted).Select(x => (long)x));

        // seq rises by exactly 1 on every price change and is "-" on max raises (R7.2).
        var publicSeqs = entries.Where(e => e.Seq != "-").Select(e => long.Parse(e.Seq)).ToList();
        publicSeqs.Should().Equal(Enumerable.Range(1, publicSeqs.Count).Select(x => (long)x));
        entries.Where(e => e.Seq == "-").Should().OnlyContain(e => e.Result == "MaxRaised");

        // Replay: feed the same bids, in stream order, to ProxyResolver.
        var increments = IncrementTable.Parse(LiveAuctionSeeder.Table1);
        var state = new BidState(0, null, 0, 0, 100);
        foreach (var entry in entries)
        {
            var resolution = ProxyResolver.Resolve(state, entry.User, entry.MaxC, increments);
            resolution.Result.ToString().Should().Be(entry.Result, $"entry eseq {entry.Eseq}");
            resolution.Next.PriceC.Should().Be(entry.PriceC, $"entry eseq {entry.Eseq}");
            resolution.Next.Leader.Should().Be(entry.Leader);
            resolution.Next.LeaderMaxC.Should().Be(entry.LeaderMaxC);
            state = resolution.Next;
        }

        (await LiveAuctionSeeder.ReadBidStateAsync(db, keys)).Should().Be(state, "Redis ends where the replay ends");
        await LiveAuctionSeeder.CleanUpAsync(db, keys);
    }
}
