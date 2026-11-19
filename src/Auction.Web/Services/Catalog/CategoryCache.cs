using Auction.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace Auction.Web.Services.Catalog;

/// <summary>The category tree, cached 5 minutes per web copy (app bar menu, browse slugs, new listing).</summary>
public sealed class CategoryCache(CatalogService.CatalogServiceClient catalog, IMemoryCache cache)
{
    private const string Key = "web:cattree";

    public async Task<IReadOnlyList<Category>> RootsAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key, out IReadOnlyList<Category>? roots) && roots != null)
        {
            return roots;
        }

        var reply = await catalog.GetCategoryTreeAsync(new GetCategoryTreeRequest(), cancellationToken: ct);
        roots = reply.Roots.ToList();
        cache.Set(Key, roots, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5), Size = 1 });
        return roots;
    }

    /// <summary>Every category, parents first, with its depth (0 = root).</summary>
    public static IEnumerable<(Category Category, int Depth)> Flatten(IEnumerable<Category> roots, int depth = 0)
    {
        foreach (var c in roots)
        {
            yield return (c, depth);
            foreach (var child in Flatten(c.Children, depth + 1))
            {
                yield return child;
            }
        }
    }

    public static Category? FindBySlug(IEnumerable<Category> roots, string? slug) =>
        string.IsNullOrEmpty(slug) ? null : Flatten(roots).Select(x => x.Category).FirstOrDefault(c => c.Slug == slug);
}
