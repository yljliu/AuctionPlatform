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

/// <summary>Seller auction actions: create (R4), edit and cancel (R5), list mine (R8).</summary>
public sealed class AuctionManager(AuctionDbContext db, SellerGuard guard, OutboxWriter outbox, ILiveAuctionControl live, TimeProvider clock)
{
    public const int CancelReasonMax = 400;

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

    /// <summary>
    /// Changes times and prices of a Scheduled auction (R5.1). Needs the row_version the seller saw.
    /// With bids: EDIT_LOCKED. Started or ended without bids: INVALID_STATE.
    /// </summary>
    public async Task<AuctionView> UpdateAsync(Guid sellerId, Guid auctionId, string? rowVersion, AuctionInput input, CancellationToken ct = default)
    {
        await guard.EnsureActiveSellerAsync(sellerId, ct);
        var auction = await LoadOwnAsync(sellerId, auctionId, ct);

        AuctionRules.EnsureEditable(auction.Status, auction.BidCount);
        var expected = ParseRowVersion(rowVersion);
        var settings = AuctionRules.Validate(input, Now);
        await EnsureIncrementTableExistsAsync(settings.IncrementTableId, ct);

        ApplySettings(auction, settings);
        auction.UpdatedAt = Now;

        outbox.Add(OutboxTopics.AuctionUpdated, auction.AuctionId.ToString(), new
        {
            auctionId,
            startMs = ToUnixMs(auction.StartTime),
            endMs = ToUnixMs(auction.EndTime),
        });

        db.Entry(auction).Property(a => a.RowVersion).OriginalValue = expected;
        await SaveOrConflictAsync(ct);

        return (await LoadViewAsync(auctionId, ct))!;
    }

    /// <summary>
    /// Seller cancel (R5.2 to R5.4): Scheduled, or Live with no bids. Sets the auction Cancelled,
    /// the item back to Draft and writes AuctionCancelled, all in one save.
    /// For a Live auction the live copy must agree first, so a bid that lands at the same moment wins.
    /// </summary>
    public async Task<AuctionView> CancelAsync(Guid sellerId, Guid auctionId, string? reason, CancellationToken ct = default)
    {
        await guard.EnsureActiveSellerAsync(sellerId, ct);

        reason = (reason ?? "").Trim();
        if (reason.Length == 0 || reason.Length > CancelReasonMax)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Give a reason for cancelling.",
                [new FieldIssue("reason", "length", $"Reason must be 1 to {CancelReasonMax} characters.")]);
        }

        var auction = await LoadOwnAsync(sellerId, auctionId, ct);
        var path = AuctionRules.EnsureCancellable(auction.Status, auction.BidCount);

        if (path == CancelPath.LiveWithoutBids && !await live.CancelAsync(auctionId, ct))
        {
            throw new DomainException(ErrorCodes.InvalidState, "A bid came in just now, so this auction can no longer be cancelled.");
        }

        var now = Now;
        var wasStatus = auction.Status;
        auction.Status = AuctionStatus.Cancelled;
        auction.CancelledReason = reason;
        auction.CancelledBy = sellerId;
        auction.UpdatedAt = now;

        var item = await db.Items.FirstAsync(i => i.ItemId == auction.ItemId, ct);
        item.Status = ItemStatus.Draft;
        item.UpdatedAt = now;

        outbox.Add(OutboxTopics.AuctionCancelled, auction.AuctionId.ToString(), new
        {
            auctionId,
            itemId = auction.ItemId,
            wasStatus = wasStatus.ToString(),
            reason,
        });

        await SaveOrConflictAsync(ct);
        return (await LoadViewAsync(auctionId, ct))!;
    }

    /// <summary>The seller's own auctions, newest first, filterable by status (R8).</summary>
    public async Task<(IReadOnlyList<AuctionView> Auctions, int Total)> ListMineAsync(Guid sellerId, string? status, Paging paging, CancellationToken ct = default)
    {
        var query = db.Auctions.AsNoTracking().Where(a => a.SellerId == sellerId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!EnumText.TryParse<AuctionStatus>(status, out var wanted))
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "Unknown auction status.",
                    [new FieldIssue("status", "one_of", $"Must be one of: {string.Join(", ", Enum.GetNames<AuctionStatus>())}.")]);
            }

            query = query.Where(a => a.Status == wanted);
        }

        var total = await query.CountAsync(ct);
        var ids = await query.OrderByDescending(a => a.CreatedAt).Skip(paging.Skip).Take(paging.Size).Select(a => a.AuctionId).ToListAsync(ct);

        var views = new List<AuctionView>();
        foreach (var id in ids)
        {
            var view = await LoadViewAsync(id, ct);
            if (view != null)
            {
                views.Add(view);
            }
        }

        return (views, total);
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

    private async Task<AuctionEntity> LoadOwnAsync(Guid sellerId, Guid auctionId, CancellationToken ct)
    {
        var auction = await db.Auctions.FirstOrDefaultAsync(a => a.AuctionId == auctionId, ct)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Auction not found.");
        if (auction.SellerId != sellerId)
        {
            throw new DomainException(ErrorCodes.NotOwner, "This is not your auction.");
        }

        return auction;
    }

    private async Task SaveOrConflictAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(ErrorCodes.VersionConflict, "This auction changed since you loaded it. Reload and try again.");
        }
    }

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
            [new FieldIssue("row_version", "required", "Send the row_version you got with the auction.")]);
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
