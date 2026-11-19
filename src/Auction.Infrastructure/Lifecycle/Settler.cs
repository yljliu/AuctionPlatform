using System.Data;
using System.Text.Json;
using Auction.Domain.Enums;
using Auction.Domain.Settlements;
using Auction.Infrastructure.Bidding.Persistence;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Auction.Infrastructure.Lifecycle;

/// <summary>What settling decided. AlreadySettled when an earlier run did it (a rerun after a crash).</summary>
public sealed record SettleResult(AuctionOutcome Outcome, decimal FinalPrice, Guid? WinnerId, Guid? SettlementId, bool AlreadySettled)
{
    public long FinalC => (long)Math.Round(FinalPrice * 100m, 0, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The settlement transaction (spec 05 design 6, R5.3 to R5.7): fencing check, outcome, Settlement row
/// when Sold, auction Settled, item Sold or Unsold, AuctionSettled outbox row, all in one commit.
/// Idempotent: a Settled auction returns its earlier outcome and writes nothing.
/// </summary>
public sealed class Settler(SqlConnectionFactory connections, ILogger<Settler> log)
{
    public const string OutboxTopic = "AuctionSettled";

    private sealed class Row
    {
        public Guid AuctionId { get; set; }
        public Guid ItemId { get; set; }
        public Guid SellerId { get; set; }
        public string Currency { get; set; } = "USD";
        public byte Status { get; set; }
        public byte? Outcome { get; set; }
        public decimal? CurrentPrice { get; set; }
        public Guid? CurrentLeaderId { get; set; }
        public int BidCount { get; set; }
        public decimal? ReservePrice { get; set; }
        public bool InstallmentsAllowed { get; set; }
        public byte ShippingType { get; set; }
        public decimal? ShippingCost { get; set; }
        public decimal? BuyerPremiumPct { get; set; }
        public decimal? SellerFinalValuePct { get; set; }
    }

    private sealed class Earlier
    {
        public Guid SettlementId { get; set; }
        public Guid BuyerId { get; set; }
        public decimal HammerPrice { get; set; }
    }

    private const string Load = """
        SELECT a.AuctionId, a.ItemId, a.SellerId, a.Currency, a.Status, a.Outcome, a.CurrentPrice, a.CurrentLeaderId,
               a.BidCount, a.ReservePrice, a.InstallmentsAllowed, i.ShippingType, i.ShippingCost,
               f.BuyerPremiumPct, f.SellerFinalValuePct
        FROM catalog.Auctions a WITH (UPDLOCK, HOLDLOCK)
        JOIN catalog.Items i ON i.ItemId = a.ItemId
        LEFT JOIN catalog.FeeSchedules f ON f.FeeScheduleId = a.FeeScheduleId
        WHERE a.AuctionId = @AuctionId;
        """;

    private const string Existing = """
        SELECT TOP 1 SettlementId, BuyerId, HammerPrice FROM pay.Settlements
        WHERE AuctionId = @AuctionId AND Status IN (0, 1, 2, 3, 4, 7);
        """;

    // UX_Settlement_Active (one active settlement per auction) is the last guard against a second row.
    private const string InsertSettlement = """
        INSERT pay.Settlements (SettlementId, AuctionId, BuyerId, SellerId, Currency, HammerPrice, BuyerPremium, ShippingCost,
            Tax, TotalDue, AmountPaid, SellerFee, InstallmentsAllowed, Status, PaymentDeadline, IsSecondChance, CreatedAt, UpdatedAt)
        VALUES (@SettlementId, @AuctionId, @BuyerId, @SellerId, @Currency, @HammerPrice, @BuyerPremium, @ShippingCost,
            @Tax, @TotalDue, 0, @SellerFee, @InstallmentsAllowed, 0, DATEADD(HOUR, @PaymentHours, SYSUTCDATETIME()), 0,
            SYSUTCDATETIME(), SYSUTCDATETIME());
        """;

    private const string MarkSettled = """
        UPDATE catalog.Auctions SET Status = 5, Outcome = @Outcome, UpdatedAt = SYSUTCDATETIME() WHERE AuctionId = @AuctionId;
        UPDATE catalog.Items SET Status = @ItemStatus, UpdatedAt = SYSUTCDATETIME() WHERE ItemId = @ItemId;
        INSERT notify.Outbox (Topic, [Key], Payload, CreatedAt) VALUES (@Topic, @Key, @Payload, SYSUTCDATETIME());
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Settles one auction. Null when it cannot be settled (not found, or Cancelled in SQL). Throws StaleFencingTokenException.</summary>
    public async Task<SettleResult?> SettleAsync(LeaseGrant grant, Guid auctionId, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        await Fencing.EnsureAsync(connection, transaction, grant, ct);
        var a = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(Load, new { AuctionId = auctionId }, transaction, cancellationToken: ct));
        if (a == null)
        {
            log.LogError("Cannot settle auction {AuctionId}: not found in SQL", auctionId);
            return null;
        }

        var status = (AuctionStatus)a.Status;
        if (status == AuctionStatus.Settled)
        {
            var earlier = await connection.QuerySingleOrDefaultAsync<Earlier>(
                new CommandDefinition(Existing, new { AuctionId = auctionId }, transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
            var outcome = (AuctionOutcome)(a.Outcome ?? (byte)AuctionOutcome.Unsold);
            return new SettleResult(outcome, earlier?.HammerPrice ?? 0, earlier?.BuyerId, earlier?.SettlementId, AlreadySettled: true);
        }

        // Live is allowed too: the SQL update after seal may have been lost; Redis is the judge of Sealed.
        if (status is not (AuctionStatus.Live or AuctionStatus.Sealed or AuctionStatus.Settling))
        {
            log.LogError("Cannot settle auction {AuctionId}: it is {Status} in SQL", auctionId, status);
            return null;
        }

        var decided = SettlementMath.Decide(a.BidCount, a.CurrentPrice, a.ReservePrice, a.CurrentLeaderId);
        Guid? settlementId = null;
        var price = decided == AuctionOutcome.Sold ? a.CurrentPrice!.Value : 0m;

        if (decided == AuctionOutcome.Sold)
        {
            if (a.BuyerPremiumPct == null || a.SellerFinalValuePct == null)
            {
                throw new InvalidOperationException($"Auction {auctionId} has no fee schedule; it cannot be settled.");
            }

            var money = SettlementMath.Compute(price, a.BuyerPremiumPct.Value, a.SellerFinalValuePct.Value,
                (ShippingType)a.ShippingType, a.ShippingCost);
            settlementId = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(InsertSettlement, new
            {
                SettlementId = settlementId,
                a.AuctionId,
                BuyerId = a.CurrentLeaderId,
                a.SellerId,
                a.Currency,
                money.HammerPrice,
                money.BuyerPremium,
                money.ShippingCost,
                money.Tax,
                money.TotalDue,
                money.SellerFee,
                a.InstallmentsAllowed,
                PaymentHours = (int)SettlementMath.PaymentWindow.TotalHours,
            }, transaction, cancellationToken: ct));
        }

        var payload = JsonSerializer.Serialize(new
        {
            auctionId,
            outcome = decided.ToString(),
            price,
            winnerId = decided == AuctionOutcome.Sold ? a.CurrentLeaderId : null,
            sellerId = a.SellerId,
            settlementId,
        }, Json);

        await connection.ExecuteAsync(new CommandDefinition(MarkSettled, new
        {
            a.AuctionId,
            Outcome = (byte)decided,
            a.ItemId,
            ItemStatus = (byte)(decided == AuctionOutcome.Sold ? ItemStatus.Sold : ItemStatus.Unsold),
            Topic = OutboxTopic,
            Key = auctionId.ToString(),
            Payload = payload,
        }, transaction, cancellationToken: ct));

        await transaction.CommitAsync(ct);
        log.LogInformation("Settled auction {AuctionId}: {Outcome} at {Price} {Currency}", auctionId, decided, price, a.Currency);
        return new SettleResult(decided, price, decided == AuctionOutcome.Sold ? a.CurrentLeaderId : null, settlementId, AlreadySettled: false);
    }
}
