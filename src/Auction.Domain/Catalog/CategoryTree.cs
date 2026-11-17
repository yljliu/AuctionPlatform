namespace Auction.Domain.Catalog;

/// <summary>One category row as stored in SQL.</summary>
public sealed record CategoryRow(int Id, int? ParentId, string Name, string Slug, string? Description, int SortOrder, bool IsActive);

/// <summary>A category with its child categories.</summary>
public sealed record CategoryNode(int Id, int? ParentId, string Name, string Slug, string? Description, int SortOrder, bool IsActive,
    IReadOnlyList<CategoryNode> Children);

/// <summary>Builds the category tree (R1.1).</summary>
public static class CategoryTree
{
    /// <summary>
    /// Nests rows by ParentId and sorts each level by SortOrder, then Name.
    /// With activeOnly, an inactive category is left out together with everything under it.
    /// Rows whose parent is missing are left out too.
    /// </summary>
    public static IReadOnlyList<CategoryNode> Build(IEnumerable<CategoryRow> rows, bool activeOnly = true)
    {
        var kept = new List<CategoryRow>();
        foreach (var row in rows)
        {
            if (!activeOnly || row.IsActive)
            {
                kept.Add(row);
            }
        }

        var byParent = new Dictionary<int, List<CategoryRow>>();
        var roots = new List<CategoryRow>();
        foreach (var row in kept)
        {
            if (row.ParentId == null)
            {
                roots.Add(row);
                continue;
            }

            if (!byParent.TryGetValue(row.ParentId.Value, out var siblings))
            {
                siblings = [];
                byParent[row.ParentId.Value] = siblings;
            }

            siblings.Add(row);
        }

        var visited = new HashSet<int>();
        return BuildLevel(roots, byParent, visited);
    }

    /// <summary>The ids of a category and all categories under it (used by browse filters).</summary>
    public static IReadOnlyList<int> SelfAndDescendants(IReadOnlyList<CategoryNode> roots, int categoryId)
    {
        var found = Find(roots, categoryId);
        if (found == null)
        {
            return [];
        }

        var ids = new List<int>();
        var todo = new Stack<CategoryNode>();
        todo.Push(found);
        while (todo.Count > 0)
        {
            var node = todo.Pop();
            ids.Add(node.Id);
            foreach (var child in node.Children)
            {
                todo.Push(child);
            }
        }

        return ids;
    }

    public static CategoryNode? Find(IReadOnlyList<CategoryNode> nodes, int categoryId)
    {
        foreach (var node in nodes)
        {
            if (node.Id == categoryId)
            {
                return node;
            }

            var inChild = Find(node.Children, categoryId);
            if (inChild != null)
            {
                return inChild;
            }
        }

        return null;
    }

    private static List<CategoryNode> BuildLevel(List<CategoryRow> level, Dictionary<int, List<CategoryRow>> byParent, HashSet<int> visited)
    {
        level.Sort(CompareRows);

        var nodes = new List<CategoryNode>();
        foreach (var row in level)
        {
            if (!visited.Add(row.Id))
            {
                continue;   // a loop in bad data; show each category once
            }

            var children = byParent.TryGetValue(row.Id, out var childRows)
                ? BuildLevel(childRows, byParent, visited)
                : [];

            nodes.Add(new CategoryNode(row.Id, row.ParentId, row.Name, row.Slug, row.Description, row.SortOrder, row.IsActive, children));
        }

        return nodes;
    }

    private static int CompareRows(CategoryRow a, CategoryRow b)
    {
        var bySort = a.SortOrder.CompareTo(b.SortOrder);
        return bySort != 0 ? bySort : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }
}
