using System.Text.Json;
using Auction.Domain.Catalog;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Catalog;

/// <summary>
/// Reads categories. The active tree is cached in Redis under cache:cattree for 10 minutes (R1.2).
/// If Redis is down, SQL still answers.
/// </summary>
public sealed class CategoryRepository(AuctionDbContext db, IConnectionMultiplexer redis, ILogger<CategoryRepository> log)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>Active categories, nested and sorted.</summary>
    public async Task<IReadOnlyList<CategoryNode>> GetTreeAsync(CancellationToken ct = default)
    {
        var cached = await TryReadCacheAsync();
        if (cached != null)
        {
            return cached;
        }

        var tree = CategoryTree.Build(await LoadRowsAsync(ct));
        await TryWriteCacheAsync(tree);
        return tree;
    }

    /// <summary>
    /// One level of categories: roots when parentId is null, otherwise that category's children.
    /// includeInactive is for admins.
    /// </summary>
    public async Task<IReadOnlyList<CategoryRow>> ListAsync(int? parentId, bool includeInactive, CancellationToken ct = default)
    {
        var query = db.Categories.AsNoTracking().Where(c => c.ParentCategoryId == parentId);
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        var rows = await query
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryRow(c.CategoryId, c.ParentCategoryId, c.Name, c.Slug, c.Description, c.SortOrder, c.IsActive))
            .ToListAsync(ct);

        return rows;
    }

    /// <summary>Call after any admin change to categories, so the next read sees it.</summary>
    public async Task InvalidateTreeAsync()
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(RedisKeys.CategoryTree);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogWarning(ex, "Could not clear the category cache; it expires within {Minutes} minutes", CacheTtl.TotalMinutes);
        }
    }

    private Task<List<CategoryRow>> LoadRowsAsync(CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .Select(c => new CategoryRow(c.CategoryId, c.ParentCategoryId, c.Name, c.Slug, c.Description, c.SortOrder, c.IsActive))
            .ToListAsync(ct);

    private async Task<IReadOnlyList<CategoryNode>?> TryReadCacheAsync()
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(RedisKeys.CategoryTree);
            return value.HasValue ? JsonSerializer.Deserialize<List<CategoryNode>>(value.ToString()) : null;
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException or JsonException)
        {
            log.LogWarning(ex, "Could not read the category cache; using SQL");
            return null;
        }
    }

    private async Task TryWriteCacheAsync(IReadOnlyList<CategoryNode> tree)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(RedisKeys.CategoryTree, JsonSerializer.Serialize(tree), CacheTtl);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogWarning(ex, "Could not write the category cache");
        }
    }
}
