using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Infrastructure.Catalog;

/// <summary>Everything needed to show one auction.</summary>
public sealed record AuctionView(
    AuctionEntity Auction,
    Item Item,
    IReadOnlyList<ItemImage> Images,
    SellerProfile? Seller,
    IReadOnlyList<BidIncrementTier> IncrementTiers);

/// <summary>Seller auction actions (R4). Edit and cancel come in task 6.</summary>
public sealed class AuctionManager(AuctionDbContext db, SellerGuard guard, OutboxWriter outbox, TimeProvider clock)
{
    private const string OneOpenAuctionIndex = "UX_OneOpenAuctionPerItem";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Creates a Scheduled auction for the seller's own Draft or Unsold item.
    /// The auction, the item status change and the AuctionCreated outbox row are saved together.
    /// </summary>
    public async Task<AuctionView> CreateAsync(Guid sellerId, Guid itemId, AuctionInput input, CancellationToken ct = default)
    {
        await guard.EnsureActiveSellerAsync(sellerId, ct);

        var item = await db.Items.FirstOrDefaultAsync(i => i.ItemId == itemId, ct)
                   ?? throw new DomainException(ErrorCodes.NotFound, "Item not found.");
        if (item.SellerId != sellerId)
        {
            throw new DomainException(ErrorCodes.NotOwner, "This is not your item.");
        }

        AuctionRules.EnsureItemCanBeListed(item.Status);
        var settings = AuctionRules.Validate(input, Now);
        await EnsureIncrementTableExistsAsync(settings.IncrementTableId, ct);

        var hasImage = await db.ItemImages.AnyAsync(i => i.ItemId == itemId && i.Status == ImageStatus.Ready, ct);
        if (!hasImage)
        {
            throw new DomainException(ErrorCodes.ImageRequired, "Add at least one photo before you list this item.");
        }

        var now = Now;
        var auction = new AuctionEntity
        {
            AuctionId = Guid.NewGuid(),
            ItemId = itemId,
            SellerId = sellerId,
            Status = AuctionStatus.Scheduled,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ApplySettings(auction, settings);
        db.Auctions.Add(auction);

        item.Status = ItemStatus.Published;
        item.UpdatedAt = now;

        outbox.Add(OutboxTopics.AuctionCreated, auction.AuctionId.ToString(), new
        {
            auctionId = auction.AuctionId,
            itemId,
            sellerId,
            startMs = ToUnixMs(auction.StartTime),
            endMs = ToUnixMs(auction.EndTime),
        });

        try
        {
            await db.SaveChangesAsync(ct);   // one transaction for all three rows
        }
        catch (DbUpdateException ex) when (IsOneOpenAuctionViolation(ex))
        {
            // Another request opened an auction for this item at the same moment.
            throw new DomainException(ErrorCodes.InvalidState, "This item already has an open auction.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(ErrorCodes.VersionConflict, "The item changed while you were listing it. Reload and try again.");
        }

        return await LoadViewAsync(auction.AuctionId, ct)
               ?? throw new InvalidOperationException("Auction vanished right after it was created.");
    }

    /// <summary>The auction with its item, images, seller and increment table, or null.</summary>
    public async Task<AuctionView?> LoadViewAsync(Guid auctionId, CancellationToken ct = default)
    {
        var auction = await db.Auctions.AsNoTracking().FirstOrDefaultAsync(a => a.AuctionId == auctionId, ct);
        if (auction == null)
        {
            return null;
        }

        var item = await db.Items.AsNoTracking().FirstAsync(i => i.ItemId == auction.ItemId, ct);
        var images = await db.ItemImages.AsNoTracking()
            .Where(i => i.ItemId == auction.ItemId && i.Status == ImageStatus.Ready)
            .OrderBy(i => i.SortOrder)
            .ToListAsync(ct);
        var seller = await db.SellerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == auction.SellerId, ct);
        var tiers = await db.BidIncrementTiers.AsNoTracking()
            .Where(t => t.TableId == auction.IncrementTableId)
            .OrderBy(t => t.FromPrice)
            .ToListAsync(ct);

        return new AuctionView(auction, item, images, seller, tiers);
    }

    // ---------- helpers ----------

    internal static void ApplySettings(AuctionEntity auction, AuctionSettings settings)
    {
        auction.StartTime = settings.StartUtc;
        auction.ScheduledEndTime = settings.EndUtc;
        auction.EndTime = settings.EndUtc;
        auction.StartingPrice = settings.StartingPrice;
        auction.ReservePrice = settings.ReservePrice;
        auction.SnipeWindowSec = settings.SnipeWindowSec;
        auction.ExtensionSec = settings.ExtensionSec;
        auction.MaxTotalExtensionSec = settings.MaxTotalExtensionSec;
        auction.IncrementTableId = settings.IncrementTableId;
        auction.FeeScheduleId = settings.FeeScheduleId;
        auction.Currency = settings.Currency;
    }

    private async Task EnsureIncrementTableExistsAsync(int tableId, CancellationToken ct)
    {
        var exists = await db.BidIncrementTiers.AnyAsync(t => t.TableId == tableId, ct);
        if (!exists)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Unknown bid increment table.",
                [new FieldIssue("increment_table_id", "exists", "Use increment table 1.")]);
        }
    }

    private static bool IsOneOpenAuctionViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql && sql.Message.Contains(OneOpenAuctionIndex, StringComparison.Ordinal);

    private static long ToUnixMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
