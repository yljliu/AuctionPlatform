namespace Auction.Domain.Enums;

public enum NotificationType : byte
{
    Outbid=1,
    Won=2,
    Lost=3,
    EndingSoon=4,
    AuctionSold=5,
    AuctionUnsold=6,
    AuctionCancelled=7,   // spec 07
    PaymentReceived=8,    // spec 10: to the buyer
    BuyerPaid=9,          // spec 10: to the seller
    BuyerDefaulted=10,    // spec 10: to the seller
    Strike=11,            // spec 10: to the buyer, 1 or 2 of 3
    AccountSuspended=12,  // spec 10: to the buyer, at 3 strikes
    SecondChance=13,      // spec 10: offer to the next bidder
    SecondChanceAnswer=14, // spec 10: accepted, declined or expired, to the seller
}
