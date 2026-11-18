using System.Buffers.Binary;
using System.Numerics;

namespace Auction.Domain.Bidding;

/// <summary>
/// Picks the Redis partition of an auction (spec 03 design 1):
/// N = Murmur3(auctionId bytes) mod Partitions.
/// The same id and count always give the same N, on every machine.
/// Never change Partitions while auctions are live: their keys would move.
/// </summary>
public static class AuctionPartitioner
{
    public const int MaxPartitions = 1024;

    public static int PartitionOf(Guid auctionId, int partitions)
    {
        if (partitions < 1 || partitions > MaxPartitions)
        {
            throw new ArgumentOutOfRangeException(nameof(partitions), partitions, $"Partitions must be 1 to {MaxPartitions}.");
        }

        Span<byte> bytes = stackalloc byte[16];
        auctionId.TryWriteBytes(bytes);   // the same 16 bytes as Guid.ToByteArray()
        return (int)(Murmur3.Hash32(bytes) % (uint)partitions);
    }
}

/// <summary>MurmurHash3 x86 32-bit (Austin Appleby). Small, fast, and the same result everywhere.</summary>
public static class Murmur3
{
    private const uint C1 = 0xcc9e2d51;
    private const uint C2 = 0x1b873593;

    public static uint Hash32(ReadOnlySpan<byte> data, uint seed = 0)
    {
        var hash = seed;
        var blocks = data.Length / 4;

        // Body: 4 bytes at a time, little-endian.
        for (var i = 0; i < blocks; i++)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4, 4));
            hash ^= MixKey(k);
            hash = BitOperations.RotateLeft(hash, 13);
            hash = (hash * 5) + 0xe6546b64;
        }

        // Tail: the last 1 to 3 bytes.
        var tail = data[(blocks * 4)..];
        uint last = 0;
        if (tail.Length >= 3)
        {
            last ^= (uint)tail[2] << 16;
        }

        if (tail.Length >= 2)
        {
            last ^= (uint)tail[1] << 8;
        }

        if (tail.Length >= 1)
        {
            last ^= tail[0];
            hash ^= MixKey(last);
        }

        // Final mix.
        hash ^= (uint)data.Length;
        hash ^= hash >> 16;
        hash *= 0x85ebca6b;
        hash ^= hash >> 13;
        hash *= 0xc2b2ae35;
        hash ^= hash >> 16;
        return hash;
    }

    private static uint MixKey(uint k)
    {
        k *= C1;
        k = BitOperations.RotateLeft(k, 15);
        k *= C2;
        return k;
    }
}
