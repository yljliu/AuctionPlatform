using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>Spec 03 task 7: the BidEseq migration on a real SQL Server.</summary>
[Collection(SqlServerCollection.Name)]
public class BidEseqMigrationTests(SqlServerFixture sql)
{
    [Fact]
    public async Task New_columns_and_the_unique_index_exist()
    {
        await using var db = await sql.CreateEmptyContextAsync("bid_eseq_migration");
        await db.Database.MigrateAsync();

        var columns = await db.Database.SqlQueryRaw<string>("""
            SELECT TABLE_SCHEMA + '.' + TABLE_NAME + '.' + COLUMN_NAME AS [Value] FROM INFORMATION_SCHEMA.COLUMNS
            WHERE (TABLE_NAME = 'Bids' AND COLUMN_NAME IN ('ESeq', 'Idx', 'Seq', 'SubSeq'))
               OR (TABLE_NAME = 'Auctions' AND COLUMN_NAME = 'LastESeq')
               OR (TABLE_NAME = 'AuctionEvents' AND COLUMN_NAME = 'ESeq')
            """).ToListAsync();

        columns.Should().BeEquivalentTo(
            "bidding.Bids.ESeq", "bidding.Bids.Idx", "bidding.Bids.Seq", "catalog.Auctions.LastESeq", "bidding.AuctionEvents.ESeq");

        var index = await db.Database.SqlQueryRaw<string>("""
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) AS [Value]
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = 'UX_Bids_ESeq' AND i.is_unique = 1
            """).ToListAsync();

        index.Should().Equal("AuctionId,ESeq,Idx");
    }

    [Fact]
    public async Task Same_eseq_and_idx_twice_is_refused()
    {
        await using var db = await sql.CreateEmptyContextAsync("bid_eseq_unique");
        await db.Database.MigrateAsync();
        var auction = Guid.NewGuid();

        async Task Insert() => await db.Database.ExecuteSqlAsync($"""
            INSERT bidding.Bids (AuctionId, UserId, ESeq, Idx, Seq, Kind, Amount, AcceptedAt)
            VALUES ({auction}, {Guid.NewGuid()}, 1, 0, 1, 0, 10, SYSUTCDATETIME())
            """);

        await Insert();
        var second = async () => await Insert();

        await second.Should().ThrowAsync<Exception>().WithMessage("*UX_Bids_ESeq*");
    }
}
