using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Domain.Tests;

/// <summary>
/// Spec 03 task 7: the EF model the BidEseq migration was made from. Reads the model only;
/// no database is opened.
/// </summary>
public class BidSchemaModelTests
{
    private static readonly AuctionDbContext Db = new(new DbContextOptionsBuilder<AuctionDbContext>()
        .UseSqlServer("Server=unused;Database=unused")
        .Options);

    private static Microsoft.EntityFrameworkCore.Metadata.IEntityType Entity<T>() => Db.Model.FindEntityType(typeof(T))!;

    [Fact]
    public void Bids_are_unique_by_auction_eseq_and_idx()
    {
        var index = Entity<Bid>().GetIndexes().Single(i => i.GetDatabaseName() == "UX_Bids_ESeq");

        index.IsUnique.Should().BeTrue();
        index.Properties.Select(p => p.Name).Should().Equal(nameof(Bid.AuctionId), nameof(Bid.ESeq), nameof(Bid.Idx));
    }

    [Fact]
    public void Old_seq_subseq_unique_index_is_gone()
    {
        Entity<Bid>().GetIndexes().Should().NotContain(i => i.GetDatabaseName() == "UX_Bids_Seq");
        Entity<Bid>().FindProperty("SubSeq").Should().BeNull();
    }

    [Fact]
    public void Seq_is_nullable_for_max_raise_rows()
    {
        Entity<Bid>().FindProperty(nameof(Bid.Seq))!.IsNullable.Should().BeTrue();
    }

    [Fact]
    public void Eseq_and_idx_are_required_columns()
    {
        Entity<Bid>().FindProperty(nameof(Bid.ESeq))!.IsNullable.Should().BeFalse();
        Entity<Bid>().FindProperty(nameof(Bid.Idx))!.GetColumnType().Should().Be("tinyint");
    }

    [Fact]
    public void Auctions_have_last_eseq_with_default_zero()
    {
        var property = Entity<AuctionEntity>().FindProperty(nameof(AuctionEntity.LastESeq))!;

        property.IsNullable.Should().BeFalse();
        property.GetDefaultValue().Should().Be(0L);
    }

    [Fact]
    public void Auction_events_have_eseq()
    {
        Entity<AuctionEvent>().FindProperty(nameof(AuctionEvent.ESeq)).Should().NotBeNull();
    }

    [Fact]
    public void Idempotency_index_is_kept()
    {
        var index = Entity<Bid>().GetIndexes().Single(i => i.GetDatabaseName() == "UX_Bids_Idem");

        index.IsUnique.Should().BeTrue();
        index.GetFilter().Should().Be("[IdempotencyKey] IS NOT NULL");
    }

    [Fact]
    public void BidEseq_is_the_latest_migration()
    {
        var migrations = Db.Database.GetMigrations().ToList();

        migrations.Should().Contain(m => m.EndsWith("_BidEseq"));
        migrations.Last().Should().EndWith("_BidEseq");
    }
}
