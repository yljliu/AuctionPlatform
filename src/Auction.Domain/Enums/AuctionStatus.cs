namespace Auction.Domain.Enums;

public enum AuctionStatus : byte
{
    Draft=0,
    Scheduled=1,
    Live=2,
    Sealed=3,
    Settling=4,
    Settled=5,
    Cancelled=6,
    Frozen=7,
}
