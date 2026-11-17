using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Auction.Infrastructure.Catalog;

public sealed record ItemPage(IReadOnlyList<ItemWithImages> Items, int Total);

public sealed record ItemWithImages(Item Item, IReadOnlyList<ItemImage> Images);

/// <summary>Seller item actions: create, update, delete draft, list mine (R2, R8).</summary>
public sealed class ItemManager(AuctionDbContext db, SellerGuard guard, TimeProvider clock, ImageStoreOptions? imageStore = null)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ItemWithImages> CreateAsync(Guid sellerId, ItemInput input, CancellationToken ct = default)
    {
        await guard.EnsureActiveSellerAsync(sellerId, ct);
        var valid = ItemRules.Validate(input);
        await EnsureCategoryUsableAsync(valid.CategoryId, ct);

        var now = Now;
        var item = new Item
        {
            ItemId = Guid.NewGuid(),
            SellerId = sellerId,
            Status = ItemStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(item, valid);

        db.Items.Add(item);
        await db.SaveChangesAsync(ct);
        return new ItemWithImages(item, []);
    }

    public async Task<ItemWithImages> UpdateAsync(Guid sellerId, Guid itemId, string? rowVersion, ItemInput input, CancellationToken ct = default)
    {
        await guard.EnsureActiveSellerAsync(sellerId, ct);
        var item = await LoadOwnAsync(sellerId, itemId, ct);

        if (item.Status is ItemStatus.Sold or ItemStatus.Removed)
        {
            throw new DomainException(ErrorCodes.InvalidState, "A sold or removed item cannot be changed.");
        }

        var expected = ParseRowVersion(rowVersion);
        var next = ItemRules.Validate(input);
        ItemRules.EnsureLockedFieldsUnchanged(item.LockedAt != null, ToValid(item), next);

        if (next.CategoryId != item.CategoryId)
        {
            await EnsureCategoryUsableAsync(next.CategoryId, ct);
        }

        Apply(item, next);
        item.UpdatedAt = Now;

        // The save only works if the row still has the version the seller saw.
        db.Entry(item).Property(i => i.RowVersion).OriginalValue = expected;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(ErrorCodes.VersionConflict, "Someone else changed this item. Reload it and try again.");
        }

        return new ItemWithImages(item, await ImagesOfAsync(item.ItemId, ct));
    }

    /// <summary>
    /// Deletes a Draft item. If it was ever in an auction (for example a cancelled one),
    /// it is marked Removed instead, so the auction history stays.
    /// </summary>
    public async Task DeleteDraftAsync(Guid sellerId, Guid itemId, CancellationToken ct = default)
    {
        await guard.EnsureActiveSellerAsync(sellerId, ct);
        var item = await LoadOwnAsync(sellerId, itemId, ct);

        if (item.Status != ItemStatus.Draft)
        {
            throw new DomainException(ErrorCodes.InvalidState, "Only draft items can be deleted.");
        }

        var hadAuction = await db.Auctions.AnyAsync(a => a.ItemId == itemId, ct);
        if (hadAuction)
        {
            item.Status = ItemStatus.Removed;
            item.UpdatedAt = Now;
        }
        else
        {
            await db.ItemImages.Where(i => i.ItemId == itemId).ExecuteDeleteAsync(ct);
            db.Items.Remove(item);
        }

        await db.SaveChangesAsync(ct);

        if (!hadAuction && imageStore != null)
        {
            var folder = Path.Combine(imageStore.RootPath, itemId.ToString());
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    public async Task<ItemPage> ListMineAsync(Guid sellerId, string? status, Paging paging, CancellationToken ct = default)
    {
        var query = db.Items.AsNoTracking().Where(i => i.SellerId == sellerId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!EnumText.TryParse<ItemStatus>(status, out var wanted))
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "Unknown item status.",
                    [new FieldIssue("status", "one_of", $"Must be one of: {string.Join(", ", Enum.GetNames<ItemStatus>())}.")]);
            }

            query = query.Where(i => i.Status == wanted);
        }
        else
        {
            query = query.Where(i => i.Status != ItemStatus.Removed);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(i => i.UpdatedAt)
            .Skip(paging.Skip).Take(paging.Size)
            .ToListAsync(ct);

        var ids = items.Select(i => i.ItemId).ToList();
        var images = await db.ItemImages.AsNoTracking()
            .Where(img => ids.Contains(img.ItemId))
            .OrderBy(img => img.SortOrder)
            .ToListAsync(ct);

        var result = new List<ItemWithImages>();
        foreach (var item in items)
        {
            result.Add(new ItemWithImages(item, images.Where(img => img.ItemId == item.ItemId).ToList()));
        }

        return new ItemPage(result, total);
    }

    // ---------- helpers ----------

    private async Task<Item> LoadOwnAsync(Guid sellerId, Guid itemId, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.ItemId == itemId, ct)
                   ?? throw new DomainException(ErrorCodes.NotFound, "Item not found.");

        if (item.SellerId != sellerId)
        {
            throw new DomainException(ErrorCodes.NotOwner, "This is not your item.");
        }

        return item;
    }

    private async Task EnsureCategoryUsableAsync(int categoryId, CancellationToken ct)
    {
        var active = await db.Categories.AnyAsync(c => c.CategoryId == categoryId && c.IsActive, ct);
        if (!active)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Unknown category.",
                [new FieldIssue("category_id", "exists", "Pick a category from the list.")]);
        }
    }

    private Task<List<ItemImage>> ImagesOfAsync(Guid itemId, CancellationToken ct) =>
        db.ItemImages.AsNoTracking().Where(i => i.ItemId == itemId).OrderBy(i => i.SortOrder).ToListAsync(ct);

    private static byte[] ParseRowVersion(string? rowVersion)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(rowVersion))
            {
                return Convert.FromBase64String(rowVersion);
            }
        }
        catch (FormatException)
        {
            // fall through
        }

        throw new DomainException(ErrorCodes.ValidationFailed, "row_version is missing or not valid.",
            [new FieldIssue("row_version", "required", "Send the row_version you got with the item.")]);
    }

    private static void Apply(Item item, ValidItem valid)
    {
        item.CategoryId = valid.CategoryId;
        item.Title = valid.Title;
        item.Description = valid.Description;
        item.Condition = valid.Condition;
        item.WarrantyType = valid.WarrantyType;
        item.WarrantyNote = valid.WarrantyNote;
        item.ShippingType = valid.ShippingType;
        item.ShippingCost = valid.ShippingCost;
        item.HandlingDays = valid.HandlingDays;
        item.ReturnsAccepted = valid.ReturnsAccepted;
        item.ReturnWindowDays = valid.ReturnWindowDays;
        item.ReturnShippingPaidBy = valid.ReturnShippingPaidBy;
    }

    private static ValidItem ToValid(Item item) => new(
        item.CategoryId, item.Title, item.Description, item.Condition, item.WarrantyType, item.WarrantyNote,
        item.ShippingType, item.ShippingCost, item.HandlingDays, item.ReturnsAccepted, item.ReturnWindowDays, item.ReturnShippingPaidBy);
}
