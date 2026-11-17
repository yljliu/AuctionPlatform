namespace Auction.Domain.Enums;

public enum SettlementStatus : byte
{
    PendingPayment=0,
    PartiallyPaid=1,
    PaidInFull=2,
    InEscrow=3,
    Released=4,
    Defaulted=5,
    Refunded=6,
    Disputed=7,
}
