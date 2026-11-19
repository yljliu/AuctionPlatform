using System.Text.Json;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Auction.Infrastructure.Identity;

/// <summary>Admin ban and suspend (requirement R5.3).</summary>
public sealed class UserAdminManager(AuctionDbContext db, UserStatusCache statusCache, TimeProvider clock)
{
    public Task BanAsync(Guid actorId, Guid userId, string? reason, string? correlationId, CancellationToken ct = default) =>
        ChangeStatusAsync(actorId, userId, UserStatus.Banned, null, reason, correlationId, "user.ban", ct);

    public Task SuspendAsync(Guid actorId, Guid userId, DateTime untilUtc, string? reason, string? correlationId, CancellationToken ct = default)
    {
        if (untilUtc <= clock.GetUtcNow().UtcDateTime)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Suspend end time must be in the future.",
                [new FieldIssue("until_ms", "future", "Pick a time in the future.")]);
        }

        return ChangeStatusAsync(actorId, userId, UserStatus.Suspended, untilUtc, reason, correlationId, "user.suspend", ct);
    }

    /// <summary>Back to Active after a ban or suspension (spec 06 admin page). Old sessions stay revoked.</summary>
    public Task ReinstateAsync(Guid actorId, Guid userId, string? reason, string? correlationId, CancellationToken ct = default) =>
        ChangeStatusAsync(actorId, userId, UserStatus.Active, null, reason, correlationId, "user.reinstate", ct);

    private async Task ChangeStatusAsync(Guid actorId, Guid userId, UserStatus status, DateTime? until,
        string? reason, string? correlationId, string action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A reason is required.",
                [new FieldIssue("reason", "required", "Enter a reason.")]);
        }

        if (actorId == userId)
        {
            throw new DomainException(ErrorCodes.InvalidState, "You cannot change your own status.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct)
                   ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");

        var now = clock.GetUtcNow().UtcDateTime;
        var before = JsonSerializer.Serialize(new { status = user.Status.ToString(), sv = user.StatusVersion });

        user.Status = status;
        user.SuspendedUntil = until;
        user.StatusVersion++;
        user.UpdatedAt = now;

        db.AuditLog.Add(new AuditLogEntry
        {
            ActorId = actorId,
            Action = action,
            TargetType = "User",
            TargetId = userId.ToString(),
            Before = before,
            After = JsonSerializer.Serialize(new { status = status.ToString(), sv = user.StatusVersion, until }),
            Reason = reason.Length <= 400 ? reason : reason[..400],
            CorrelationId = correlationId,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);

        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, (DateTime?)now), ct);

        await statusCache.SetAsync(userId, new UserStatusInfo(status, user.StatusVersion));
    }
}
