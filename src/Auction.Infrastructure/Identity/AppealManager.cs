using System.Text.Json;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Auction.Infrastructure.Identity;

public sealed record AppealStatusView(Appeal? Latest, UserStatus UserStatus, int ActiveStrikes);

public sealed record AppealQueueRow(
    Appeal Appeal,
    string Username,
    string Email,
    UserStatus UserStatus,
    string? SuspendReason,
    IReadOnlyList<StrikeRow> Strikes);

public sealed record StrikeRow(Guid SettlementId, string Title, DateTime CreatedAt);

/// <summary>
/// Appeals (spec 10 R7): a suspended user proves who they are with email and password (without signing in) and writes
/// to the admins. Accepting reinstates the user and clears their active strikes; rejecting keeps the suspension.
/// </summary>
public sealed class AppealManager(AuctionDbContext db, AuthManager auth, UserAdminManager admin, TimeProvider clock)
{
    public const int MessageMin = 20;
    public const int MessageMax = 2000;
    public const int NoteMax = 400;

    public async Task<AppealStatusView> SubmitAsync(string? email, string? password, string? message, CancellationToken ct = default)
    {
        var user = await auth.VerifyPasswordAsync(email, password, ct);
        if (user.Status != UserStatus.Suspended)
        {
            throw new DomainException(ErrorCodes.InvalidState, "Your account is not suspended.");
        }

        var text = (message ?? "").Trim();
        if (text.Length is < MessageMin or > MessageMax)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Check your message.",
                [new FieldIssue("message", "length", $"Write {MessageMin} to {MessageMax} characters.")]);
        }

        db.Appeals.Add(new Appeal { UserId = user.UserId, Message = text, Status = AppealStatus.Open, CreatedAt = clock.GetUtcNow().UtcDateTime });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DomainException(ErrorCodes.AlreadyApplied, "You already have an open appeal.");
        }

        return await StatusForAsync(user.UserId, user.Status, ct);
    }

    /// <summary>The user's latest appeal and status (R7.7). Works for any account with the right password.</summary>
    public async Task<AppealStatusView> GetStatusAsync(string? email, string? password, CancellationToken ct = default)
    {
        var user = await auth.VerifyPasswordAsync(email, password, ct);
        return await StatusForAsync(user.UserId, user.Status, ct);
    }

    /// <summary>Open appeals first, newest first, with why the user was suspended and their active strikes (R7.4).</summary>
    public async Task<IReadOnlyList<AppealQueueRow>> ListAsync(bool openOnly, int take = 100, CancellationToken ct = default)
    {
        var appeals = await db.Appeals.AsNoTracking()
            .Where(a => !openOnly || a.Status == AppealStatus.Open)
            .OrderBy(a => a.Status).ThenByDescending(a => a.CreatedAt)
            .Take(take).ToListAsync(ct);

        var userIds = appeals.Select(a => a.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, ct);
        var ids = userIds.Select(id => id.ToString()).ToList();
        var reasons = (await db.AuditLog.AsNoTracking()
                .Where(x => x.TargetType == "User" && ids.Contains(x.TargetId) && x.Action.StartsWith("user.suspend"))
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.TargetId, x.Reason, x.Action })
                .ToListAsync(ct))
            .GroupBy(x => x.TargetId).ToDictionary(g => g.Key, g => g.First().Reason ?? g.First().Action);
        var strikes = (await (from s in db.UserStrikes.AsNoTracking()
                              join st in db.Settlements.AsNoTracking() on s.SettlementId equals st.SettlementId
                              join a in db.Auctions.AsNoTracking() on st.AuctionId equals a.AuctionId
                              join i in db.Items.AsNoTracking() on a.ItemId equals i.ItemId
                              where userIds.Contains(s.UserId) && s.ClearedAt == null
                              orderby s.CreatedAt
                              select new { s.UserId, s.SettlementId, i.Title, s.CreatedAt }).ToListAsync(ct))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<StrikeRow>)g.Select(x => new StrikeRow(x.SettlementId, x.Title, x.CreatedAt)).ToList());

        return appeals.Select(a =>
        {
            var u = users.GetValueOrDefault(a.UserId);
            return new AppealQueueRow(a, u?.Username ?? "?", u?.Email ?? "", u?.Status ?? UserStatus.Active,
                reasons.GetValueOrDefault(a.UserId.ToString()), strikes.GetValueOrDefault(a.UserId) ?? []);
        }).ToList();
    }

    /// <summary>Accept: user Active, strikes cleared. Reject: still suspended; a note is required (R7.5, R7.6).</summary>
    public async Task ResolveAsync(Guid adminId, long appealId, bool accept, string? note, string? correlationId, CancellationToken ct = default)
    {
        var appeal = await db.Appeals.FirstOrDefaultAsync(a => a.AppealId == appealId, ct)
                     ?? throw new DomainException(ErrorCodes.NotFound, "Appeal not found.");
        if (appeal.Status != AppealStatus.Open)
        {
            throw new DomainException(ErrorCodes.InvalidState, "This appeal is already decided.");
        }

        var text = note?.Trim();
        if (!accept && string.IsNullOrEmpty(text))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A note is required to reject.",
                [new FieldIssue("note", "required", "Tell the user why.")]);
        }

        if (text is { Length: > NoteMax })
        {
            text = text[..NoteMax];
        }

        var now = clock.GetUtcNow().UtcDateTime;

        // Accept: strikes and status first, the appeal last. A crash in between leaves the appeal Open,
        // and accepting again is safe (strikes already cleared, reinstate only when still suspended).
        if (accept)
        {
            await db.UserStrikes.Where(s => s.UserId == appeal.UserId && s.ClearedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClearedAt, (DateTime?)now), ct);
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.UserId == appeal.UserId, ct);
            if (user.Status == UserStatus.Suspended)
            {
                await admin.ReinstateAsync(adminId, appeal.UserId, "appeal accepted" + (string.IsNullOrEmpty(text) ? "" : ": " + text), correlationId, ct);
            }
        }

        appeal.Status = accept ? AppealStatus.Accepted : AppealStatus.Rejected;
        appeal.AdminId = adminId;
        appeal.AdminNote = text;
        appeal.ResolvedAt = now;
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorId = adminId, Action = accept ? "appeal.accept" : "appeal.reject", TargetType = "User", TargetId = appeal.UserId.ToString(),
            After = JsonSerializer.Serialize(new { appealId }), Reason = text, CorrelationId = correlationId, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<AppealStatusView> StatusForAsync(Guid userId, UserStatus status, CancellationToken ct)
    {
        var latest = await db.Appeals.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(ct);
        var strikes = await db.UserStrikes.CountAsync(s => s.UserId == userId && s.ClearedAt == null, ct);
        return new AppealStatusView(latest, status, strikes);
    }
}
