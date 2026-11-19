using System.Text.Json;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Auction.Infrastructure.Catalog;

/// <summary>
/// Admin actions on a running auction (spec 05 task 5, R6.2 to R6.5): force-cancel, force-close,
/// freeze and resume. Redis changes first (its script checks the status), then SQL, then one AuditLog row.
/// SQL rows change with a status check instead of the row version, because the persister writes
/// the same auction row while bids come in.
/// </summary>
public sealed class AuctionControlManager(AuctionDbContext db, ILiveAuctionControl live, OutboxWriter outbox, TimeProvider clock)
{
    public const int ReasonMax = 400;

    public async Task ForceCancelAsync(Guid adminId, Guid auctionId, string? reason, string? correlationId, CancellationToken ct = default)
    {
        reason = RequireReason(reason);
        var auction = await LoadAsync(auctionId, ct);
        RequireStatus(auction, AuctionStatus.Scheduled, AuctionStatus.Live, AuctionStatus.Frozen);

        Refused((await live.CancelAsync(auctionId, force: true, ct)).Result, allowNotInRedis: true);

        await SaveAsync(auction, AuctionStatus.Cancelled, "auction.force_cancel", adminId, reason, correlationId, ct, cancel: true);
    }

    public async Task ForceCloseAsync(Guid adminId, Guid auctionId, string? reason, string? correlationId, CancellationToken ct = default)
    {
        reason = RequireReason(reason);
        var auction = await LoadAsync(auctionId, ct);
        RequireStatus(auction, AuctionStatus.Live);

        // No SQL status change: the lifecycle loop seals it on its next tick.
        Refused((await live.CloseAsync(auctionId, ct)).Result, allowNotInRedis: false);

        await SaveAsync(auction, null, "auction.force_close", adminId, reason, correlationId, ct);
    }

    public async Task FreezeAsync(Guid adminId, Guid auctionId, string? reason, string? correlationId, CancellationToken ct = default)
    {
        reason = RequireReason(reason);
        var auction = await LoadAsync(auctionId, ct);
        RequireStatus(auction, AuctionStatus.Live);

        Refused((await live.FreezeAsync(auctionId, ct)).Result, allowNotInRedis: true);

        await SaveAsync(auction, AuctionStatus.Frozen, "auction.freeze", adminId, reason, correlationId, ct);
    }

    public async Task ResumeAsync(Guid adminId, Guid auctionId, string? reason, string? correlationId, CancellationToken ct = default)
    {
        reason = RequireReason(reason);
        var auction = await LoadAsync(auctionId, ct);
        RequireStatus(auction, AuctionStatus.Frozen);

        // Bids need the Redis copy, so resume refuses until it exists (the leader rebuilds lost ones).
        var answer = await live.ResumeAsync(auctionId, ct);
        Refused(answer.Result, allowNotInRedis: false);

        var newEnd = answer.EndMs is long endMs ? DateTimeOffset.FromUnixTimeMilliseconds(endMs).UtcDateTime : (DateTime?)null;
        await SaveAsync(auction, AuctionStatus.Live, "auction.resume", adminId, reason, correlationId, ct, newEnd: newEnd);
    }

    private async Task SaveAsync(AuctionRow auction, AuctionStatus? to, string action, Guid adminId, string reason,
        string? correlationId, CancellationToken ct, bool cancel = false, DateTime? newEnd = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            if (to != null)
            {
                var changed = await db.Auctions
                    .Where(a => a.AuctionId == auction.AuctionId && a.Status == auction.Status)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.Status, to.Value)
                        .SetProperty(a => a.EndTime, a => newEnd ?? a.EndTime)
                        .SetProperty(a => a.CancelledReason, a => cancel ? reason : a.CancelledReason)
                        .SetProperty(a => a.CancelledBy, a => cancel ? adminId : a.CancelledBy)
                        .SetProperty(a => a.UpdatedAt, now), ct);
                if (changed == 0)
                {
                    throw new DomainException(ErrorCodes.InvalidState, "The auction changed just now. Reload and try again.");
                }
            }

            if (cancel)
            {
                await db.Items.Where(i => i.ItemId == auction.ItemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ItemStatus.Draft).SetProperty(i => i.UpdatedAt, now), ct);
                outbox.Add(OutboxTopics.AuctionCancelled, auction.AuctionId.ToString(), new
                {
                    auctionId = auction.AuctionId,
                    itemId = auction.ItemId,
                    wasStatus = auction.Status.ToString(),
                    reason,
                    forced = true,
                });
            }

            db.AuditLog.Add(new AuditLogEntry
            {
                ActorId = adminId,
                Action = action,
                TargetType = "Auction",
                TargetId = auction.AuctionId.ToString(),
                Before = JsonSerializer.Serialize(new { status = auction.Status.ToString() }),
                After = JsonSerializer.Serialize(new { status = (to ?? auction.Status).ToString(), endTime = newEnd }),
                Reason = reason,
                CorrelationId = correlationId,
                CreatedAt = now,
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }

    private sealed record AuctionRow(Guid AuctionId, Guid ItemId, AuctionStatus Status);

    private async Task<AuctionRow> LoadAsync(Guid auctionId, CancellationToken ct) =>
        await db.Auctions.AsNoTracking()
            .Where(a => a.AuctionId == auctionId)
            .Select(a => new AuctionRow(a.AuctionId, a.ItemId, a.Status))
            .FirstOrDefaultAsync(ct)
        ?? throw new DomainException(ErrorCodes.NotFound, "Auction not found.");

    private static void RequireStatus(AuctionRow auction, params AuctionStatus[] allowed)
    {
        if (!allowed.Contains(auction.Status))
        {
            throw new DomainException(ErrorCodes.InvalidState, $"This action is not allowed while the auction is {auction.Status}.");
        }
    }

    private static void Refused(LiveControlResult result, bool allowNotInRedis)
    {
        if (result == LiveControlResult.InvalidState || (result == LiveControlResult.NotInRedis && !allowNotInRedis))
        {
            throw new DomainException(ErrorCodes.InvalidState, "The live auction is not in a state that allows this action.");
        }
    }

    private static string RequireReason(string? reason)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length == 0 || reason.Length > ReasonMax)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A reason is required.",
                [new FieldIssue("reason", "length", $"Reason must be 1 to {ReasonMax} characters.")]);
        }

        return reason;
    }
}
