using System.Text.Json;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Domain.Identity;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Auction.Infrastructure.Identity;

/// <summary>A user's seller application as they or an admin see it. Status is null when they never applied.</summary>
public sealed record SellerApplicationView(Guid UserId, string Username, string Email, SellerStatus? Status, string? DisplayName, string? RejectReason, bool IsSeller);

/// <summary>
/// Become a seller (design W9): a bidder applies with a shop name, an admin approves or rejects.
/// The application is the user's SellerProfiles row (Applied, then Verified or Rejected); approving adds the Seller role.
/// </summary>
public sealed class SellerApplicationManager(AuctionDbContext db, TimeProvider clock)
{
    public async Task<SellerApplicationView> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Roles).FirstOrDefaultAsync(u => u.UserId == userId, ct)
                   ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");
        var profile = await db.SellerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
        return View(user, profile);
    }

    public async Task<SellerApplicationView> ApplyAsync(Guid userId, string? displayName, CancellationToken ct = default)
    {
        var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.UserId == userId, ct)
                   ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");
        if (user.Status != UserStatus.Active)
        {
            throw new DomainException(ErrorCodes.InvalidState, "Only active accounts can apply to sell.");
        }

        if (user.Roles.Any(r => r.Role == Role.Seller))
        {
            throw new DomainException(ErrorCodes.InvalidState, "You are already a seller.");
        }

        if (SellerApplicationRules.CheckDisplayName(displayName) is { } issue)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Check the shop name.", [issue]);
        }

        var name = SellerApplicationRules.Clean(displayName);
        var upper = name.ToUpperInvariant();
        if (await db.SellerProfiles.AnyAsync(p => p.UserId != userId && p.DisplayName.ToUpper() == upper, ct))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Check the shop name.",
                [new FieldIssue("display_name", "taken", "That shop name is taken.")]);
        }

        var profile = await db.SellerProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        switch (profile?.Status)
        {
            case SellerStatus.Applied:
                throw new DomainException(ErrorCodes.AlreadyApplied, "Your application is waiting for an admin.");
            case SellerStatus.Revoked:
                throw new DomainException(ErrorCodes.InvalidState, "Selling was turned off for this account. Contact the admins.");
            case null:
                profile = new SellerProfile { UserId = userId };
                db.SellerProfiles.Add(profile);
                break;
        }

        // New, or again after a rejection.
        profile.DisplayName = name;
        profile.Status = SellerStatus.Applied;
        profile.RejectReason = null;
        profile.VerifiedAt = null;
        profile.VerifiedBy = null;
        Audit(userId, "seller.apply", userId, name, null);
        await db.SaveChangesAsync(ct);
        return View(user, profile);
    }

    /// <summary>Applications waiting for an admin, oldest first.</summary>
    public async Task<IReadOnlyList<SellerApplicationView>> ListPendingAsync(CancellationToken ct = default)
    {
        var rows = await (from p in db.SellerProfiles.AsNoTracking()
                          join u in db.Users.AsNoTracking() on p.UserId equals u.UserId
                          where p.Status == SellerStatus.Applied
                          orderby u.CreatedAt
                          select new { p, u }).ToListAsync(ct);
        return rows.Select(x => new SellerApplicationView(x.u.UserId, x.u.Username, x.u.Email, x.p.Status, x.p.DisplayName, null, false)).ToList();
    }

    /// <summary>Approve: Verified and the Seller role. Reject: a reason is required; the user may apply again.</summary>
    public async Task ReviewAsync(Guid adminId, Guid userId, bool approve, string? reason, string? correlationId, CancellationToken ct = default)
    {
        var profile = await db.SellerProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Application not found.");
        if (profile.Status != SellerStatus.Applied)
        {
            throw new DomainException(ErrorCodes.InvalidState, "This application is already decided.");
        }

        var text = reason?.Trim();
        if (!approve && string.IsNullOrEmpty(text))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A reason is required to reject.",
                [new FieldIssue("reason", "required", "Tell the user why.")]);
        }

        if (text is { Length: > SellerApplicationRules.ReasonMax })
        {
            text = text[..SellerApplicationRules.ReasonMax];
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (approve)
        {
            profile.Status = SellerStatus.Verified;
            profile.VerifiedAt = now;
            profile.VerifiedBy = adminId;
            profile.RejectReason = null;
            if (!await db.UserRoles.AnyAsync(r => r.UserId == userId && r.Role == Role.Seller, ct))
            {
                db.UserRoles.Add(new UserRole { UserId = userId, Role = Role.Seller, GrantedAt = now });
            }
        }
        else
        {
            profile.Status = SellerStatus.Rejected;
            profile.RejectReason = text;
        }

        Audit(adminId, approve ? "seller.approve" : "seller.reject", userId, profile.DisplayName, text, correlationId);
        await db.SaveChangesAsync(ct);
    }

    private void Audit(Guid actor, string action, Guid userId, string displayName, string? reason, string? correlationId = null) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorId = actor, Action = action, TargetType = "User", TargetId = userId.ToString(),
            After = JsonSerializer.Serialize(new { displayName }), Reason = reason, CorrelationId = correlationId,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        });

    private static SellerApplicationView View(User user, SellerProfile? profile) =>
        new(user.UserId, user.Username, user.Email, profile?.Status, profile?.DisplayName, profile?.RejectReason,
            user.Roles.Any(r => r.Role == Role.Seller));
}
