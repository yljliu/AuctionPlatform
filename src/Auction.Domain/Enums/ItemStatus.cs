namespace Auction.Domain.Enums;

public enum ItemStatus : byte
{
    Draft=0,
    Published=1,
    InAuction=2,
    Sold=3,
    Unsold=4,
    Removed=5,
}
