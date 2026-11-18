using Auction.Domain.Bidding;
using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auction.Infrastructure.Bidding;

/// <summary>Loads an increment table from catalog.BidIncrementTiers (spec 03 task 1).</summary>
public sealed class IncrementTableRepository(AuctionDbContext db)
{
    /// <summary>
    /// The table with this id. Throws InvalidOperationException when it has no rows or bad rows,
    /// because an auction cannot take bids without a valid table.
    /// </summary>
    public async Task<IncrementTable> LoadAsync(int tableId, CancellationToken ct = default)
    {
        var rows = await db.BidIncrementTiers.AsNoTracking()
            .Where(t => t.TableId == tableId)
            .Select(t => new { t.FromPrice, t.Increment })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"Increment table {tableId} has no tiers.");
        }

        try
        {
            return IncrementTable.FromMoney(rows.Select(r => (r.FromPrice, r.Increment)));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Increment table {tableId} is not valid: {ex.Message}", ex);
        }
    }
}
