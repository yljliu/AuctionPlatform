using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>Spec 03 task 1: loading increment tables from catalog.BidIncrementTiers.</summary>
[Collection(SqlServerCollection.Name)]
public class IncrementTableRepositoryTests(SqlServerFixture sql) : IAsyncLifetime
{
    private AuctionDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("increment_tables");
        await _db.Database.MigrateAsync();

        _db.BidIncrementTiers.AddRange(
            new BidIncrementTier { TableId = 1, FromPrice = 1000, Increment = 10 },
            new BidIncrementTier { TableId = 1, FromPrice = 0, Increment = 1 },
            new BidIncrementTier { TableId = 1, FromPrice = 250, Increment = 5 },
            new BidIncrementTier { TableId = 1, FromPrice = 5000, Increment = 50 },
            new BidIncrementTier { TableId = 1, FromPrice = 50, Increment = 2.5m },
            new BidIncrementTier { TableId = 2, FromPrice = 10, Increment = 1 });   // bad: no tier at 0
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Loads_table_1_sorted_in_cents()
    {
        var table = await new IncrementTableRepository(_db).LoadAsync(1);

        table.ToRedisString().Should().Be("0:100,5000:250,25000:500,100000:1000,500000:5000");
    }

    [Fact]
    public async Task Missing_table_throws()
    {
        var act = () => new IncrementTableRepository(_db).LoadAsync(99);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*99 has no tiers*");
    }

    [Fact]
    public async Task Bad_table_throws_with_its_id()
    {
        var act = () => new IncrementTableRepository(_db).LoadAsync(2);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*2 is not valid*");
    }
}
