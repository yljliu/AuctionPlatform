using Auction.Domain.Catalog;
using Auction.Infrastructure.Catalog;
using Auction.Domain.Enums;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Infrastructure.Seed;

/// <summary>
/// A fuller demo catalog: 24 items with real-looking text, 2 drawn photos each, and Scheduled auctions
/// (spec 06 and 07 demo data). Most start 2 to 7 minutes after seeding, so the lifecycle leader loads and
/// opens them the normal way (Redis and SQL agree); a few start later to fill the Scheduled lists.
/// Safe to run again: stable ids, and items that already exist are skipped.
/// </summary>
public sealed class DemoCatalog(AuctionDbContext db, string imagesRoot, DateTime nowUtc)
{
    public sealed record Entry(
        string Key,
        string Seller,
        string Category,
        PictureKind Picture,
        string Title,
        string Description,
        ItemCondition Condition,
        decimal StartingPrice,
        decimal? Reserve,
        ShippingType Shipping,
        decimal? ShippingCost,
        bool Returns,
        TimeSpan StartsIn,
        TimeSpan RunsFor);

    private static readonly TimeSpan M = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan H = TimeSpan.FromHours(1);
    private static readonly TimeSpan D = TimeSpan.FromDays(1);

    private static TimeSpan Soon(int n) => TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(15 * n);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        // ---------- watches ----------
        new("w1", "seller1", "watches-vintage", PictureKind.Watch, "1964 Omega Constellation, pie-pan dial",
            "Automatic calibre 561, serviced in 2023. Original pie-pan dial with light patina, case lightly polished. Comes with a newer leather strap.",
            ItemCondition.Good, 900m, 1400m, ShippingType.BuyerPays, 25m, true, Soon(0), 15 * M),
        new("w2", "seller2", "watches-vintage", PictureKind.Watch, "Longines Flagship, 1970s, gold-plated",
            "Runs well and keeps good time. Some wear on the plating at the lugs. Original crown.",
            ItemCondition.Fair, 250m, null, ShippingType.BuyerPays, 15m, false, Soon(1), 45 * M),
        new("w3", "seller3", "watches-vintage", PictureKind.Watch, "Seiko 6139 chronograph \"Pogue\"",
            "Yellow dial, original bezel insert with fading. Chronograph works and resets to zero.",
            ItemCondition.Good, 400m, 550m, ShippingType.FreeShipping, null, true, Soon(2), 2 * H),
        new("w4", "seller1", "watches-modern", PictureKind.Watch, "Tudor Black Bay 58, box and papers",
            "Bought in 2021, worn a few times. Full kit with both straps. No dings on the bezel.",
            ItemCondition.LikeNew, 2400m, 2900m, ShippingType.FreeShipping, null, true, Soon(3), 1 * D),
        new("w5", "seller2", "watches-modern", PictureKind.Watch, "Casio G-Shock GW-M5610, solar",
            "Solar and radio time sync. Light scuffs on the resin, screen is clean.",
            ItemCondition.Good, 40m, null, ShippingType.BuyerPays, 8m, false, Soon(4), 30 * M),
        new("w6", "seller3", "watches-modern", PictureKind.Watch, "Hamilton Khaki Field Mechanical 38 mm",
            "Hand wound, 80 hour reserve. New old stock, still has the stickers.",
            ItemCondition.New, 320m, null, ShippingType.BuyerPays, 12m, true, 3 * H, 3 * D),

        // ---------- art ----------
        new("a1", "seller2", "art-paintings", PictureKind.Painting, "Oil on canvas: hills at sunset, 60 x 45 cm",
            "Signed lower right. Framed in oak. A warm, calm landscape painted in 1998 by a local artist.",
            ItemCondition.Good, 180m, 300m, ShippingType.BuyerPays, 35m, false, Soon(5), 2 * H),
        new("a2", "seller3", "art-paintings", PictureKind.Painting, "Small plein-air study, river valley",
            "Oil on board, 20 x 25 cm, gilt frame. Painted outdoors in one sitting.",
            ItemCondition.LikeNew, 75m, null, ShippingType.BuyerPays, 15m, true, Soon(6), 20 * M),
        new("a3", "seller1", "art-paintings", PictureKind.Painting, "Large abstract landscape, acrylic, 100 x 80 cm",
            "Bold colours, gallery-wrapped canvas, ready to hang. Local pickup or buyer arranges a courier.",
            ItemCondition.New, 450m, 700m, ShippingType.LocalPickupOnly, null, false, Soon(7), 6 * H),
        new("a4", "seller2", "art-prints", PictureKind.Print, "Signed city skyline screen print, 12/50",
            "Hand-pulled four-colour print, numbered and signed. Framed behind glass.",
            ItemCondition.LikeNew, 60m, null, ShippingType.BuyerPays, 18m, true, Soon(8), 15 * M),
        new("a5", "seller3", "art-prints", PictureKind.Print, "Night skyline giclee print, A2, unframed",
            "Archival paper, shipped rolled in a tube. Open edition.",
            ItemCondition.New, 25m, null, ShippingType.FreeShipping, null, true, Soon(9), 1 * H),
        new("a6", "seller1", "art-prints", PictureKind.Print, "Vintage railway poster reprint, 1950s style",
            "Lithograph reprint, 70 x 50 cm, light corner wear.",
            ItemCondition.Fair, 30m, null, ShippingType.BuyerPays, 10m, false, 1 * D, 2 * D),

        // ---------- coins ----------
        new("c1", "seller3", "collectibles-coins", PictureKind.Coin, "1921 Morgan silver dollar",
            "Philadelphia mint. Nice luster with light toning. Not graded.",
            ItemCondition.Good, 45m, null, ShippingType.BuyerPays, 5m, true, Soon(10), 30 * M),
        new("c2", "seller1", "collectibles-coins", PictureKind.Coin, "1 oz gold Krugerrand, 1978",
            "Full ounce of gold. Small marks from storage. Ships insured.",
            ItemCondition.Good, 1800m, 2100m, ShippingType.BuyerPays, 20m, false, Soon(11), 3 * H),
        new("c3", "seller2", "collectibles-coins", PictureKind.Coin, "Roman bronze follis, Constantine I",
            "About 1700 years old. Clear portrait, green patina. Comes in a coin flip.",
            ItemCondition.Fair, 35m, null, ShippingType.BuyerPays, 5m, false, Soon(12), 45 * M),
        new("c4", "seller3", "collectibles-coins", PictureKind.Coin, "Set of 10 wheat pennies, 1940s",
            "Mixed dates and mints, circulated. A fun starter set.",
            ItemCondition.Fair, 5m, null, ShippingType.FreeShipping, null, true, Soon(13), 10 * M),

        // ---------- cards ----------
        new("k1", "seller1", "collectibles-cards", PictureKind.Card, "Rookie card, graded 9 Mint",
            "Sealed in a grading slab, label and case are clean. A great centrepiece card.",
            ItemCondition.LikeNew, 120m, 200m, ShippingType.BuyerPays, 6m, false, Soon(14), 1 * H),
        new("k2", "seller2", "collectibles-cards", PictureKind.Card, "Holo trading card, first edition",
            "Raw card in a top-loader. Sharp corners, light scratch on the holo.",
            ItemCondition.Good, 80m, null, ShippingType.BuyerPays, 4m, true, Soon(15), 25 * M),
        new("k3", "seller3", "collectibles-cards", PictureKind.Card, "1990s basketball insert card, graded 8",
            "Slabbed, near mint. Bright colours.",
            ItemCondition.Good, 55m, null, ShippingType.BuyerPays, 6m, false, Soon(16), 2 * H),
        new("k4", "seller1", "collectibles-cards", PictureKind.Card, "Lot of 50 vintage baseball cards",
            "1970s and 1980s commons and a few stars. Played-with condition.",
            ItemCondition.Fair, 20m, null, ShippingType.BuyerPays, 7m, false, Soon(17), 15 * M),
        new("k5", "seller2", "collectibles-cards", PictureKind.Card, "Signed football card with certificate",
            "On-card autograph with a certificate of authenticity.",
            ItemCondition.LikeNew, 150m, 220m, ShippingType.FreeShipping, null, true, 6 * H, 1 * D),
        new("k6", "seller3", "collectibles-cards", PictureKind.Card, "Sealed booster box, 2003 set",
            "Factory sealed, never opened. Box has light shelf wear.",
            ItemCondition.New, 600m, 800m, ShippingType.BuyerPays, 15m, false, 2 * D, 3 * D),
        new("k7", "seller1", "collectibles-cards", PictureKind.Card, "Graded 10 Gem Mint parallel card",
            "Low population grade. Perfect centring.",
            ItemCondition.New, 300m, null, ShippingType.BuyerPays, 6m, true, Soon(18), 6 * H),
        new("k8", "seller2", "collectibles-cards", PictureKind.Card, "Hockey rookie card, raw",
            "Clean surface, slight off-centre. In a penny sleeve and top-loader.",
            ItemCondition.Good, 15m, null, ShippingType.FreeShipping, null, false, Soon(19), 20 * M),
    ];

    /// <summary>Adds the catalog items, photos and auctions that are not there yet. Returns how many were added.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var added = 0;
        foreach (var entry in Entries)
        {
            var itemId = SeedRunner.StableId("catalog-item:" + entry.Key);
            if (await db.Items.AnyAsync(i => i.ItemId == itemId, ct))
            {
                continue;
            }

            var seller = await db.Users.AsNoTracking().FirstAsync(u => u.Username == entry.Seller, ct);
            var category = await db.Categories.AsNoTracking().FirstAsync(c => c.Slug == entry.Category, ct);
            db.Items.Add(new Item
            {
                ItemId = itemId, SellerId = seller.UserId, CategoryId = category.CategoryId,
                Title = entry.Title, Description = entry.Description, Condition = entry.Condition,
                WarrantyType = WarrantyType.None, ShippingType = entry.Shipping,
                ShippingCost = entry.Shipping == ShippingType.BuyerPays ? entry.ShippingCost : null,
                HandlingDays = 2, ReturnsAccepted = entry.Returns, ReturnWindowDays = entry.Returns ? (byte)14 : null,
                ReturnShippingPaidBy = entry.Returns ? ReturnShippingPaidBy.Buyer : null,
                Status = ItemStatus.InAuction, CreatedAt = nowUtc, UpdatedAt = nowUtc,
            });

            await WritePicturesAsync(db, imagesRoot, itemId, entry.Picture, entry.Key, nowUtc, ct);

            var start = nowUtc + entry.StartsIn;
            var end = start + entry.RunsFor;
            db.Auctions.Add(new AuctionEntity
            {
                AuctionId = SeedRunner.StableId("catalog-auction:" + entry.Key), ItemId = itemId, SellerId = seller.UserId,
                Currency = "USD", StartTime = start, ScheduledEndTime = end, EndTime = end,
                StartingPrice = entry.StartingPrice, ReservePrice = entry.Reserve,
                IncrementTableId = AuctionRules.DefaultIncrementTableId, FeeScheduleId = AuctionRules.DefaultFeeScheduleId,
                SnipeWindowSec = 60, ExtensionSec = 60, MaxTotalExtensionSec = 600,
                Status = AuctionStatus.Scheduled, CreatedAt = nowUtc, UpdatedAt = nowUtc,
            });

            await db.SaveChangesAsync(ct);
            added++;
        }

        return added;
    }

    /// <summary>Gives drawn photos to items that have none (the 6 original demo items too).</summary>
    public async Task<int> AddMissingPicturesAsync(IEnumerable<(Guid ItemId, PictureKind Kind, string Key)> items, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var (itemId, kind, key) in items)
        {
            if (!await db.Items.AnyAsync(i => i.ItemId == itemId, ct) || await db.ItemImages.AnyAsync(i => i.ItemId == itemId, ct))
            {
                continue;
            }

            await WritePicturesAsync(db, imagesRoot, itemId, kind, key, nowUtc, ct);
            await db.SaveChangesAsync(ct);
            count++;
        }

        return count;
    }

    // Writes {root}/{itemId}/{imageId}.jpg and {imageId}_thumb.jpg, the same layout ImageManager uses.
    // Also used by AdminService.CreateDemoAuctions (spec 08). Adds the rows; the caller saves.
    public static async Task WritePicturesAsync(AuctionDbContext db, string imagesRoot, Guid itemId, PictureKind kind, string key, DateTime nowUtc, CancellationToken ct, int count = 2)
    {
        var folder = Path.Combine(imagesRoot, itemId.ToString());
        Directory.CreateDirectory(folder);
        for (byte variant = 0; variant < count; variant++)
        {
            var imageId = SeedRunner.StableId($"catalog-image:{key}:{variant}");
            var bytes = DemoPictures.Draw(kind, key, variant);
            var (width, height, thumb) = Thumbnails.Make(bytes, ImageSniffer.Jpeg, ImageManager.ThumbSize, ImageManager.MaxPixels);
            await File.WriteAllBytesAsync(Path.Combine(folder, $"{imageId}.jpg"), bytes, ct);
            await File.WriteAllBytesAsync(Path.Combine(folder, $"{imageId}_thumb.jpg"), thumb, ct);
            db.ItemImages.Add(new ItemImage
            {
                ImageId = imageId, ItemId = itemId, StorageKey = $"{itemId}/{imageId}.jpg", ContentType = "image/jpeg",
                Bytes = bytes.Length, Width = width, Height = height, SortOrder = variant, Status = ImageStatus.Ready, CreatedAt = nowUtc,
            });
        }
    }
}
