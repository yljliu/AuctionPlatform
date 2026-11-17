using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auction.Integration.Tests;

[Collection(SqlServerCollection.Name)]
public class CategoryRepositoryTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
{
    private AuctionDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("categories");
        await _db.Database.MigrateAsync();
        await redis.Connection.GetDatabase().KeyDeleteAsync(RedisKeys.CategoryTree);

        var art = new Category { Name = "Art", Slug = "art", SortOrder = 2 };
        var watches = new Category { Name = "Watches", Slug = "watches", SortOrder = 1 };
        _db.Categories.AddRange(art, watches);
        await _db.SaveChangesAsync();

        _db.Categories.AddRange(
            new Category { Name = "Prints", Slug = "art-prints", ParentCategoryId = art.CategoryId, SortOrder = 1 },
            new Category { Name = "Paintings", Slug = "art-paintings", ParentCategoryId = art.CategoryId, SortOrder = 0 },
            new Category { Name = "Hidden", Slug = "art-hidden", ParentCategoryId = art.CategoryId, IsActive = false });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await redis.Connection.GetDatabase().KeyDeleteAsync(RedisKeys.CategoryTree);
        await _db.DisposeAsync();
    }

    private CategoryRepository Repo() => new(_db, redis.Connection, NullLogger<CategoryRepository>.Instance);

    [Fact]
    public async Task Tree_nests_sorts_and_hides_inactive()
    {
        var tree = await Repo().GetTreeAsync();

        tree.Select(c => c.Name).Should().Equal("Watches", "Art");
        tree[1].Children.Select(c => c.Name).Should().Equal("Paintings", "Prints");
    }

    [Fact]
    public async Task Tree_is_cached_until_cleared()
    {
        var repo = Repo();
        await repo.GetTreeAsync();   // fills the cache

        await _db.Categories.Where(c => c.Slug == "watches").ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, "Clocks"));

        (await repo.GetTreeAsync()).Select(c => c.Name).Should().Contain("Watches", "the cached copy is used");
        (await redis.Connection.GetDatabase().KeyTimeToLiveAsync(RedisKeys.CategoryTree)).Should().BeCloseTo(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));

        await repo.InvalidateTreeAsync();

        (await repo.GetTreeAsync()).Select(c => c.Name).Should().Contain("Clocks");
    }

    [Fact]
    public async Task List_returns_one_level()
    {
        var art = await _db.Categories.SingleAsync(c => c.Slug == "art");

        (await Repo().ListAsync(null, includeInactive: false)).Select(c => c.Name).Should().Equal("Watches", "Art");
        (await Repo().ListAsync(art.CategoryId, includeInactive: false)).Select(c => c.Name).Should().Equal("Paintings", "Prints");
        (await Repo().ListAsync(art.CategoryId, includeInactive: true)).Select(c => c.Name).Should().Contain("Hidden");
    }
}
