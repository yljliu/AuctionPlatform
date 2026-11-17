using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auction.Infrastructure.Catalog;

/// <summary>
/// Checks that the caller may sell right now: Active, has the Seller role, and a Verified seller profile.
/// The Api also checks the role from the token; this check uses the database, so a role or ban
/// change takes effect at once even while an old token is still valid.
/// </summary>
public sealed class SellerGuard(AuctionDbContext db, UserStatusCache statusCache)
{
    public async Task EnsureActiveSellerAsync(Guid userId, CancellationToken ct = default)
    {
        var status = await statusCache.GetAsync(userId, ct)
                     ?? throw new DomainException(ErrorCodes.Unauthenticated, "Please sign in again.");

        if (status.Status == UserStatus.Banned)
        {
            throw new DomainException(ErrorCodes.AccountBanned, "This account is banned.");
        }

        if (status.Status == UserStatus.Suspended)
        {
            throw new DomainException(ErrorCodes.AccountSuspended, "This account is suspended.");
        }

        var isSeller = await db.UserRoles.AnyAsync(r => r.UserId == userId && r.Role == Role.Seller, ct);
        if (!isSeller)
        {
            throw new DomainException(ErrorCodes.RoleRequired, "You need a seller account for this.");
        }

        var verified = await db.SellerProfiles.AnyAsync(p => p.UserId == userId && p.Status == SellerStatus.Verified, ct);
        if (!verified)
        {
            throw new DomainException(ErrorCodes.SellerNotVerified, "Your seller account is not approved yet.");
        }
    }
}
