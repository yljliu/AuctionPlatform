namespace Auction.Domain.Enums;

public enum BidKind : byte
{
    Manual=0,
    ProxyAuto=1,
    AutoOutbid=2,
    MaxRaise=3,
}
