namespace Auction.Domain.Catalog;

/// <summary>Page numbers start at 1. Size defaults to 20, max 100.</summary>
public readonly record struct Paging(int Page, int Size)
{
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    public int Skip => (Page - 1) * Size;

    public static Paging From(int page, int size) =>
        new(page < 1 ? 1 : page, size < 1 ? DefaultSize : Math.Min(size, MaxSize));
}
