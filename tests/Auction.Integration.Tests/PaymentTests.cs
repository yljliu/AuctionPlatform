using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Account;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Payments;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>Spec 10 task 2: the fake payment API on a real SQL Server and Redis.</summary>
[Collection(SqlServerCollection.Name)]
public class PaymentTests(SqlServerFixture sql, RedisFixture redis)
{
    private static readonly DemoCardInput Good = new("1234 5678 9012 3456", "12/30", "123", "Alice Smith");
    private static readonly DemoCardInput Short = new("1234 5678 9012 345", "12/30", "123", "Alice Smith");

    private PaymentManager Payments(LifecycleTestDb t, Infrastructure.Persistence.AuctionDbContext? db = null)
    {
        db ??= t.NewContext();
        return new PaymentManager(db, new SettlementQueries(db), new OutboxWriter(db, TimeProvider.System),
            new RedisRateLimiter(redis.Connection, NullLogger<RedisRateLimiter>.Instance), redis.Connection,
            TimeProvider.System, NullLogger<PaymentManager>.Instance);
    }

    /// <summary>A settled auction with a PendingPayment settlement for a new buyer (a new id, so the card-try limit starts empty).</summary>
    private static async Task<(Guid Settlement, Guid Buyer)> WonAsync(LifecycleTestDb t, double deadlineInHours = 72)
    {
        var buyer = Guid.NewGuid();
        var a = await t.AddAuctionAsync(new(AuctionStatus.Settled, CurrentPrice: 100m, Leader: buyer, BidCount: 1));
        var now = DateTime.UtcNow;
        var s = new Settlement
        {
            SettlementId = Guid.NewGuid(), AuctionId = a.AuctionId, BuyerId = buyer, SellerId = LifecycleTestDb.Seller, Currency = "USD",
            HammerPrice = 100m, BuyerPremium = 5m, ShippingCost = 0m, Tax = 0m, TotalDue = 105m, SellerFee = 8m,
            Status = SettlementStatus.PendingPayment, PaymentDeadline = now.AddHours(deadlineInHours), CreatedAt = now, UpdatedAt = now,
        };
        t.Db.Settlements.Add(s);
        await t.Db.SaveChangesAsync();
        return (s.SettlementId, buyer);
    }

    private static async Task<DomainException> Fails(Func<Task> call) =>
        (await call.Should().ThrowAsync<DomainException>()).Which;

    [Fact]
    public async Task A_good_card_pays_in_full_and_keeps_only_the_last_4()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "pay_good");
        var (sid, buyer) = await WonAsync(t);

        var receipt = await Payments(t).PayDemoAsync(buyer, sid, Good, Guid.NewGuid());

        receipt.Repeat.Should().BeFalse();
        receipt.Amount.Should().Be(105m);
        receipt.CardLabel.Should().Be("Demo card •••• 3456");
        receipt.Reference.Should().StartWith("PAY-").And.HaveLength(12);

        await using var db = t.NewContext();
        var s = await db.Settlements.SingleAsync(x => x.SettlementId == sid);
        s.Status.Should().Be(SettlementStatus.PaidInFull);
        s.AmountPaid.Should().Be(105m);
        var p = await db.Payments.SingleAsync(x => x.SettlementId == sid);
        p.CardLast4.Should().Be("3456");
        (await db.AuditLog.CountAsync(x => x.Action == "payment.demo" && x.TargetId == sid.ToString())).Should().Be(1);
        (await db.Outbox.CountAsync(x => x.Topic == PaymentTopics.PaymentReceived && x.Key == sid.ToString())).Should().Be(1);
        (await db.AuditLog.Where(x => x.TargetId == sid.ToString()).Select(x => x.After).SingleAsync()).Should().NotContain("1234");
    }

    [Fact]
    public async Task A_15_digit_card_is_refused_with_the_field_and_changes_nothing()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "pay_short");
        var (sid, buyer) = await WonAsync(t);

        var error = await Fails(() => Payments(t).PayDemoAsync(buyer, sid, Short, Guid.NewGuid()));

        error.Code.Should().Be(ErrorCodes.ValidationFailed);
        error.Fields.Should().ContainSingle(f => f.Field == "card_number" && f.Message == "Card number must be 16 digits.");
        await using var db = t.NewContext();
        (await db.Settlements.SingleAsync(x => x.SettlementId == sid)).Status.Should().Be(SettlementStatus.PendingPayment);
        (await db.Payments.AnyAsync(x => x.SettlementId == sid)).Should().BeFalse();
    }

    [Fact]
    public async Task The_6th_bad_card_is_rate_limited_and_then_even_a_good_card_waits()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "pay_limit");
        var (sid, buyer) = await WonAsync(t);

        for (var i = 0; i < 5; i++)
        {
            (await Fails(() => Payments(t).PayDemoAsync(buyer, sid, Short, Guid.NewGuid()))).Code.Should().Be(ErrorCodes.ValidationFailed);
        }

        var sixth = await Fails(() => Payments(t).PayDemoAsync(buyer, sid, Short, Guid.NewGuid()));
        sixth.Code.Should().Be(ErrorCodes.RateLimited);
        sixth.RetryAfterMs.Should().BeGreaterThan(0);
        sixth.Message.Should().StartWith("Too many tries. Try again in");

        (await Fails(() => Payments(t).PayDemoAsync(buyer, sid, Good, Guid.NewGuid()))).Code.Should().Be(ErrorCodes.RateLimited);
    }

    [Fact]
    public async Task Paying_again_after_success_returns_the_same_receipt_without_checking_the_card()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "pay_again");
        var (sid, buyer) = await WonAsync(t);
        var first = await Payments(t).PayDemoAsync(buyer, sid, Good, Guid.NewGuid());

        var again = await Payments(t).PayDemoAsync(buyer, sid, Short, Guid.NewGuid());

        again.Repeat.Should().BeTrue();
        again.PaymentId.Should().Be(first.PaymentId);
        await using var db = t.NewContext();
        (await db.Payments.CountAsync(x => x.SettlementId == sid)).Should().Be(1);
    }

    [Fact]
    public async Task The_same_key_twice_gives_one_payment_and_on_another_purchase_is_a_mismatch()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "pay_key");
        var (sid, buyer) = await WonAsync(t);
        var key = Guid.NewGuid();

        var first = await Payments(t).PayDemoAsync(buyer, sid, Good, key);
        var retry = await Payments(t).PayDemoAsync(buyer, sid, Good, key);
        retry.PaymentId.Should().Be(first.PaymentId);
        retry.Repeat.Should().BeTrue();

        // A second win for the same buyer, paid with the old key.
        var a = await t.AddAuctionAsync(new(AuctionStatus.Settled, CurrentPrice: 50m, Leader: buyer, BidCount: 1));
        var other = new Settlement
        {
            SettlementId = Guid.NewGuid(), AuctionId = a.AuctionId, BuyerId = buyer, SellerId = LifecycleTestDb.Seller, Currency = "USD",
            HammerPrice = 50m, TotalDue = 50m, Status = SettlementStatus.PendingPayment, PaymentDeadline = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.Settlements.Add(other);
        await t.Db.SaveChangesAsync();

        (await Fails(() => Payments(t).PayDemoAsync(buyer, other.SettlementId, Good, key))).Code.Should().Be(ErrorCodes.IdempotencyMismatch);
    }

    [Fact]
    public async Task Someone_else_cannot_pay_and_a_missing_one_is_not_found()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "pay_owner");
        var (sid, _) = await WonAsync(t);

        (await Fails(() => Payments(t).PayDemoAsync(Guid.NewGuid(), sid, Good, Guid.NewGuid()))).Code.Should().Be(ErrorCodes.NotOwner);
        (await Fails(() => Payments(t).PayDemoAsync(Guid.NewGuid(), Guid.NewGuid(), Good, Guid.NewGuid()))).Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task After_the_deadline_it_is_too_late()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "pay_late");
        var (sid, buyer) = await WonAsync(t, deadlineInHours: -1);

        (await Fails(() => Payments(t).PayDemoAsync(buyer, sid, Good, Guid.NewGuid()))).Code.Should().Be(ErrorCodes.DeadlinePassed);
    }

    [Fact]
    public async Task Two_payments_at_once_record_one_and_both_get_its_receipt()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "pay_race");
        var (sid, buyer) = await WonAsync(t);

        var calls = Enumerable.Range(0, 4).Select(_ => Task.Run(() => Payments(t).PayDemoAsync(buyer, sid, Good, Guid.NewGuid()))).ToList();
        var receipts = await Task.WhenAll(calls);

        receipts.Select(r => r.PaymentId).Distinct().Should().ContainSingle();
        receipts.Count(r => !r.Repeat).Should().BeLessThanOrEqualTo(1);
        await using var db = t.NewContext();
        (await db.Payments.CountAsync(x => x.SettlementId == sid)).Should().Be(1);
    }

    [Fact]
    public async Task Receipt_and_payable_show_the_payment()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "pay_receipt");
        var (sid, buyer) = await WonAsync(t);
        (await Fails(() => Payments(t).GetReceiptAsync(buyer, sid))).Code.Should().Be(ErrorCodes.NotFound);

        var paid = await Payments(t).PayDemoAsync(buyer, sid, Good, Guid.NewGuid());

        (await Payments(t).GetReceiptAsync(buyer, sid)).PaymentId.Should().Be(paid.PaymentId);
        var row = await Payments(t).GetPayableAsync(buyer, sid);
        row.PaymentId.Should().Be(paid.PaymentId);
        row.PaidAt.Should().NotBeNull();
    }
}
