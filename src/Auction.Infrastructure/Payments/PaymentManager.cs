using System.Diagnostics.Metrics;
using System.Text.Json;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Domain.Payments;
using Auction.Infrastructure.Account;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Payments;

/// <summary>payments_total{result} (spec 10 R8.1, meter "Auction.Payments").</summary>
public static class PaymentMetrics
{
    public const string MeterName = "Auction.Payments";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Payments = Meter.CreateCounter<long>("payments_total");

    /// <summary>Workers: settlements that passed their deadline unpaid, and the strikes they gave (R5).</summary>
    public static readonly Counter<long> Defaulted = Meter.CreateCounter<long>("payments_defaulted_total");

    public static readonly Counter<long> Strikes = Meter.CreateCounter<long>("strikes_total");

    /// <summary>second_chance_total{result}: sent, accepted, declined, expired (R6).</summary>
    public static readonly Counter<long> SecondChance = Meter.CreateCounter<long>("second_chance_total");

    public static void Count(string result) => Payments.Add(1, new KeyValuePair<string, object?>("result", result));
}

public static class PaymentTopics
{
    public const string PaymentReceived = "PaymentReceived";
}

/// <summary>What the user typed into the card form. Never logged (the masking policy drops Number and Cvc).</summary>
public sealed record DemoCardInput(string? Number, string? Expiry, string? Cvc, string? Name);

public sealed record PaymentReceiptView(
    Guid PaymentId,
    string Reference,
    SettlementListRow Settlement,
    decimal Amount,
    string CardLast4,
    DateTime PaidAt,
    bool Repeat)
{
    public string CardLabel => $"Demo card •••• {CardLast4}";
}

/// <summary>
/// The fake payment API (spec 10 design 5): checks the card, then pays the whole total in one SQL transaction.
/// Any 16-digit card is approved; nothing is charged anywhere.
/// </summary>
public sealed class PaymentManager(
    AuctionDbContext db,
    SettlementQueries settlements,
    OutboxWriter outbox,
    IRateLimiter limiter,
    IConnectionMultiplexer redis,
    TimeProvider clock,
    ILogger<PaymentManager> log)
{
    /// <summary>5 invalid cards, then about one more every 2 minutes (R2.7: 5 per 10 minutes).</summary>
    public static readonly RateLimitPolicy InvalidCards = new("paycard", 5, 5 / 600.0);

    /// <summary>The settlement for its buyer, or NOT_FOUND / NOT_OWNER.</summary>
    public async Task<SettlementListRow> GetPayableAsync(Guid userId, Guid settlementId, CancellationToken ct = default)
    {
        var row = await settlements.ByIdAsync(settlementId, ct)
                  ?? throw new DomainException(ErrorCodes.NotFound, "Purchase not found.");
        return row.BuyerId == userId ? row : throw new DomainException(ErrorCodes.NotOwner, "This is not your purchase.");
    }

    public async Task<PaymentReceiptView> GetReceiptAsync(Guid userId, Guid settlementId, CancellationToken ct = default)
    {
        var row = await GetPayableAsync(userId, settlementId, ct);
        return row.PaymentId == null
            ? throw new DomainException(ErrorCodes.NotFound, "This purchase is not paid yet.")
            : Receipt(row, repeat: false);
    }

    public async Task<PaymentReceiptView> PayDemoAsync(Guid userId, Guid settlementId, DemoCardInput card, Guid idempotencyKey, CancellationToken ct = default)
    {
        if (idempotencyKey == Guid.Empty)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A payment key is required.",
                [new FieldIssue("idempotency_key", "required", "Missing payment key.")]);
        }

        var row = await GetPayableAsync(userId, settlementId, ct);

        // Already paid: the same success again, whatever card or key (R3.3).
        if (row.PaymentId != null)
        {
            PaymentMetrics.Count("repeat");
            return Receipt(row, repeat: true);
        }

        // The same key used for another purchase (R3.2).
        if (await db.Payments.AsNoTracking().AnyAsync(p => p.BuyerId == userId && p.IdempotencyKey == idempotencyKey, ct))
        {
            PaymentMetrics.Count("rejected");
            throw new DomainException(ErrorCodes.IdempotencyMismatch, "This payment key was used for another purchase.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (row.Status != SettlementStatus.PendingPayment)
        {
            PaymentMetrics.Count("rejected");
            throw new DomainException(ErrorCodes.InvalidState, "This purchase can no longer be paid.");
        }

        if (now > row.PaymentDeadline)
        {
            PaymentMetrics.Count("rejected");
            throw new DomainException(ErrorCodes.DeadlinePassed, "The time to pay has passed.");
        }

        await ThrowIfBlockedAsync(userId);

        var issues = FakeCardCheck.Check(card.Number, card.Expiry, card.Cvc, card.Name, DateOnly.FromDateTime(now));
        if (issues.Count > 0)
        {
            await CountInvalidAsync(userId);
            PaymentMetrics.Count("invalid");
            throw new DomainException(ErrorCodes.ValidationFailed, "Check the card details.", issues);
        }

        var last4 = FakeCardCheck.Last4(card.Number!);
        bool saved;
        try
        {
            saved = await SaveAsync(userId, settlementId, row.BuyerHandle, last4, idempotencyKey, now, ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || IsDuplicateKey(ex))
        {
            saved = false;
        }

        if (!saved)
        {
            // Another call paid it first (R3.4): give back that payment.
            db.ChangeTracker.Clear();
            var after = await GetPayableAsync(userId, settlementId, ct);
            if (after.PaymentId == null)
            {
                throw new DomainException(ErrorCodes.InvalidState, "This purchase can no longer be paid.");
            }

            PaymentMetrics.Count("repeat");
            return Receipt(after, repeat: true);
        }

        PaymentMetrics.Count("approved");
        log.LogInformation("Demo payment for settlement {SettlementId} approved, card ending {Last4}", settlementId, last4);
        var paid = await GetPayableAsync(userId, settlementId, ct);
        return Receipt(paid, repeat: false);
    }

    /// <summary>Saves the payment. False when the settlement is no longer PendingPayment (another call paid it).</summary>
    private async Task<bool> SaveAsync(Guid userId, Guid settlementId, string buyerHandle, string last4, Guid key, DateTime now, CancellationToken ct)
    {
        var s = await db.Settlements.FirstAsync(x => x.SettlementId == settlementId, ct);
        if (s.Status != SettlementStatus.PendingPayment)
        {
            return false;
        }

        var payment = new Payment
        {
            PaymentId = Guid.NewGuid(), SettlementId = s.SettlementId, BuyerId = userId, Amount = s.TotalDue - s.AmountPaid,
            Currency = s.Currency, Method = "DemoCard", CardLast4 = last4, IdempotencyKey = key, CreatedAt = now,
        };
        db.Payments.Add(payment);

        s.AmountPaid = s.TotalDue;
        s.Status = SettlementStatus.PaidInFull;
        s.UpdatedAt = now;

        db.AuditLog.Add(new AuditLogEntry
        {
            ActorId = userId, Action = "payment.demo", TargetType = "Settlement", TargetId = s.SettlementId.ToString(),
            After = JsonSerializer.Serialize(new { paymentId = payment.PaymentId, amount = payment.Amount, last4 }),
            CreatedAt = now,
        });
        outbox.Add(PaymentTopics.PaymentReceived, s.SettlementId.ToString(), new
        {
            settlementId = s.SettlementId,
            auctionId = s.AuctionId,
            buyerId = s.BuyerId,
            sellerId = s.SellerId,
            buyerHandle,
            amount = payment.Amount,
            currency = s.Currency,
            paymentId = payment.PaymentId,
        });

        await db.SaveChangesAsync(ct);   // one transaction: payment, settlement, audit, outbox
        return true;
    }

    // ---------- invalid-card limit (R2.7) ----------

    private static RedisKey BlockKey(Guid userId) => RedisKeys.RateLimit("paycard-block", userId.ToString());

    private async Task ThrowIfBlockedAsync(Guid userId)
    {
        TimeSpan? left;
        try
        {
            left = await redis.GetDatabase().KeyTimeToLiveAsync(BlockKey(userId));
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogWarning(ex, "Could not read the card-try block; allowing the payment");
            return;
        }

        if (left is { } wait && wait > TimeSpan.Zero)
        {
            PaymentMetrics.Count("rate_limited");
            throw TooMany((long)wait.TotalMilliseconds);
        }
    }

    private async Task CountInvalidAsync(Guid userId)
    {
        var result = await limiter.TryAcquireAsync(InvalidCards, userId.ToString());
        if (result.Allowed)
        {
            return;
        }

        var wait = Math.Max(1000, result.RetryAfterMs);
        try
        {
            await redis.GetDatabase().StringSetAsync(BlockKey(userId), "1", TimeSpan.FromMilliseconds(wait));
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogWarning(ex, "Could not save the card-try block");
        }

        PaymentMetrics.Count("rate_limited");
        throw TooMany(wait);
    }

    private static DomainException TooMany(long waitMs)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(waitMs / 60000.0));
        return new DomainException(ErrorCodes.RateLimited, $"Too many tries. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.")
        {
            RetryAfterMs = waitMs,
        };
    }

    private static PaymentReceiptView Receipt(SettlementListRow row, bool repeat) =>
        new(row.PaymentId!.Value, "PAY-" + row.PaymentId.Value.ToString("N")[..8].ToUpperInvariant(), row,
            row.AmountPaid, row.CardLast4 ?? "????", row.PaidAt ?? row.CreatedAt, repeat);

    private static bool IsDuplicateKey(Exception ex) =>
        ex is DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } };
}
