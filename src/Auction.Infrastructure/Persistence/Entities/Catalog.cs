using Auction.Domain.Enums;

namespace Auction.Infrastructure.Persistence.Entities;

public class Category
{
    public int CategoryId { get; set; }
    public int? ParentCategoryId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Item
{
    public Guid ItemId { get; set; }
    public Guid SellerId { get; set; }
    public int CategoryId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public ItemCondition Condition { get; set; }
    public WarrantyType WarrantyType { get; set; }
    public string? WarrantyNote { get; set; }
    public ShippingType ShippingType { get; set; }
    public decimal? ShippingCost { get; set; }
    public byte HandlingDays { get; set; }
    public bool ReturnsAccepted { get; set; }
    public byte? ReturnWindowDays { get; set; }
    public ReturnShippingPaidBy? ReturnShippingPaidBy { get; set; }
    public ItemStatus Status { get; set; }
    public DateTime? LockedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class ItemImage
{
    public Guid ImageId { get; set; }
    public Guid ItemId { get; set; }
    public string StorageKey { get; set; } = "";
    public string ContentType { get; set; } = "";
    public int Bytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public byte SortOrder { get; set; }
    public ImageStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class BidIncrementTier
{
    public int TableId { get; set; }
    public decimal FromPrice { get; set; }
    public decimal Increment { get; set; }
}

public class FeeSchedule
{
    public int FeeScheduleId { get; set; }
    public decimal BuyerPremiumPct { get; set; }
    public decimal SellerFinalValuePct { get; set; }
    public decimal ListingFee { get; set; }
    public DateTime EffectiveFrom { get; set; }
}

public class Auction
{
    public Guid AuctionId { get; set; }
    public Guid ItemId { get; set; }
    public Guid SellerId { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTime StartTime { get; set; }
    public DateTime ScheduledEndTime { get; set; }
    public DateTime EndTime { get; set; }
    public decimal StartingPrice { get; set; }
    public decimal? ReservePrice { get; set; }
    public decimal? BuyNowPrice { get; set; }
    public int IncrementTableId { get; set; } = 1;
    public int FeeScheduleId { get; set; }
    public int SnipeWindowSec { get; set; } = 60;
    public int ExtensionSec { get; set; } = 60;
    public int MaxTotalExtensionSec { get; set; } = 3600;
    public bool InstallmentsAllowed { get; set; }
    public AuctionStatus Status { get; set; }
    public AuctionOutcome? Outcome { get; set; }
    public decimal? CurrentPrice { get; set; }
    public Guid? CurrentLeaderId { get; set; }
    public int BidCount { get; set; }
    public long LastSeq { get; set; }
    public string? CancelledReason { get; set; }
    public Guid? CancelledBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
