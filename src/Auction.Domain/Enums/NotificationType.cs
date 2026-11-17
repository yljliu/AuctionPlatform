namespace Auction.Domain.Enums;

public enum NotificationType : byte
{
    Outbid=1,
    Won=2,
    Lost=3,
    EndingSoon=4,
    AuctionSold=5,
    AuctionUnsold=6,
}
