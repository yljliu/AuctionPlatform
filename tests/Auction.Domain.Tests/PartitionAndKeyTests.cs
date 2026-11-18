using System.Text;
using Auction.Domain.Bidding;
using Auction.Infrastructure.Redis;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 03 task 3: Murmur3, stable partition mapping, and the {pN} Redis keys.</summary>
public class PartitionAndKeyTests
{
    private static readonly Guid Auction = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    // ---------- Murmur3 against published values ----------

    [Theory]
    [InlineData("", 0u, 0x00000000u)]
    [InlineData("", 1u, 0x514E28B7u)]
    [InlineData("", 0xFFFFFFFFu, 0x81F16F39u)]
    [InlineData("test", 0u, 0xBA6BD213u)]
    [InlineData("Hello, world!", 1234u, 0xFAF6CDB3u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0u, 0x2E4FF723u)]
    public void Murmur3_matches_known_values(string text, uint seed, uint expected)
    {
        Murmur3.Hash32(Encoding.UTF8.GetBytes(text), seed).Should().Be(expected);
    }

    [Theory]
    [InlineData(new byte[] { 0x21 }, 0x72661CF4u)]                  // 1 tail byte
    [InlineData(new byte[] { 0x21, 0x43 }, 0xA0F7B07Au)]            // 2 tail bytes
    [InlineData(new byte[] { 0x21, 0x43, 0x65 }, 0x7E4A8634u)]      // 3 tail bytes
    [InlineData(new byte[] { 0x21, 0x43, 0x65, 0x87 }, 0xF55B516Bu)] // one full block
    [InlineData(new byte[] { 0, 0, 0, 0 }, 0x2362F9DEu)]
    public void Murmur3_handles_every_tail_length(byte[] data, uint expected)
    {
        Murmur3.Hash32(data, 0).Should().Be(expected);
    }

    // ---------- partition mapping ----------

    [Fact]
    public void One_partition_is_always_zero()
    {
        for (var i = 0; i < 1000; i++)
        {
            AuctionPartitioner.PartitionOf(Guid.NewGuid(), 1).Should().Be(0);
        }
    }

    [Fact]
    public void Same_id_always_maps_to_the_same_partition()
    {
        var first = AuctionPartitioner.PartitionOf(Auction, 16);

        for (var i = 0; i < 100; i++)
        {
            AuctionPartitioner.PartitionOf(Guid.Parse(Auction.ToString()), 16).Should().Be(first);
        }
    }

    [Fact]
    public void Mapping_is_the_hash_of_the_guid_bytes()
    {
        var expected = (int)(Murmur3.Hash32(Auction.ToByteArray()) % 7);

        AuctionPartitioner.PartitionOf(Auction, 7).Should().Be(expected);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void Ids_spread_evenly_over_partitions(int partitions)
    {
        const int ids = 20_000;
        var counts = new int[partitions];
        var random = new Random(42);   // fixed seed: the test gives the same result every run
        var bytes = new byte[16];

        for (var i = 0; i < ids; i++)
        {
            random.NextBytes(bytes);
            counts[AuctionPartitioner.PartitionOf(new Guid(bytes), partitions)]++;
        }

        var fair = ids / partitions;
        counts.Should().OnlyContain(c => c > fair * 0.85 && c < fair * 1.15, "each partition gets close to its fair share");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public void Partition_count_out_of_range_is_refused(int partitions)
    {
        var act = () => AuctionPartitioner.PartitionOf(Auction, partitions);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---------- Redis keys ----------

    [Fact]
    public void One_partition_gives_the_p0_keys()
    {
        var keys = RedisKeys.For(Auction, 1);

        keys.Tag.Should().Be("{p0}");
        keys.State.Should().Be($"{{p0}}:a:{Auction}:state");
        keys.Max.Should().Be($"{{p0}}:a:{Auction}:max");
        keys.Bids.Should().Be($"{{p0}}:a:{Auction}:bids");
        keys.Events.Should().Be($"{{p0}}:a:{Auction}:events");
        keys.Idempotency("k1").Should().Be($"{{p0}}:a:{Auction}:idem:k1");
        keys.Stream.Should().Be("{p0}:stream");
        keys.DeadLetter.Should().Be("{p0}:dlq");
    }

    [Fact]
    public void Every_key_of_an_auction_has_the_same_tag()
    {
        var keys = RedisKeys.For(Auction, 16);
        var all = new[] { keys.State, keys.Max, keys.Bids, keys.Events, keys.Idempotency("x"), keys.Stream, keys.DeadLetter };

        // Redis Cluster hashes only the text inside the first {...}, so one tag means one slot.
        all.Select(HashTag).Should().AllBe($"p{keys.Partition}");
    }

    [Fact]
    public void Keys_use_the_mapped_partition()
    {
        var partition = AuctionPartitioner.PartitionOf(Auction, 16);

        RedisKeys.For(Auction, 16).Tag.Should().Be($"{{p{partition}}}");
        RedisKeys.For(Auction, new RedisOptions { Partitions = 16 }).Should().Be(RedisKeys.For(Auction, 16));
    }

    [Fact]
    public void Old_helpers_match_the_new_keys()
    {
        RedisKeys.AuctionState(Auction).Should().Be(RedisKeys.For(Auction, 1).State);
        RedisKeys.AuctionBids(Auction).Should().Be(RedisKeys.For(Auction, 1).Bids);
        RedisKeys.AuctionMax(Auction).Should().Be(RedisKeys.For(Auction, 1).Max);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1024, true)]
    [InlineData(0, false)]
    [InlineData(2000, false)]
    public void Options_check_the_partition_count(int partitions, bool ok)
    {
        var act = () => new RedisOptions { Partitions = partitions }.Validate();

        if (ok)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<InvalidOperationException>();
        }
    }

    private static string HashTag(string key)
    {
        var open = key.IndexOf('{');
        var close = key.IndexOf('}', open + 1);
        return key[(open + 1)..close];
    }
}
