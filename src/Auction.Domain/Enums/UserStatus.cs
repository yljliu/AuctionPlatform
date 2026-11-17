namespace Auction.Domain.Enums;

public enum UserStatus : byte
{
    PendingVerification=0,
    Active=1,
    Suspended=2,
    Banned=3,
}
