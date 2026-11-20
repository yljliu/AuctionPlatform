using System.Data;
using System.Text.Json;
using Auction.Domain.Enums;
using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Lifecycle;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auction.Infrastructure.Payments;

public sealed record DefaultResult(Guid SettlementId, Guid BuyerId, int ActiveStrikes, bool Suspended, Guid? NextBidder);

/// <summary>
/// Missed payments (spec 10 R5, design W12). Leader only, fenced like settling: a settlement past its deadline
/// becomes Defaulted, the auction outcome Defaulted, the buyer gets a strike, and 3 active strikes suspend the buyer
/// until an appeal is accepted. Each settlement is its own transaction; a rerun adds nothing (one strike per settlement).
/// </summary>
public sealed class DefaultSweeper(SqlConnectionFactory connections, IServiceScopeFactory scopes, ILogger<DefaultSweeper> log)
{
    public const string DefaultedTopic = "PaymentDefaulted";
    public const int StrikesToSuspend = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Due = """
        SELECT TOP (@Take) SettlementId FROM pay.Settlements
        WHERE Status = 0 AND PaymentDeadline < SYSUTCDATETIME()
        ORDER BY PaymentDeadline;
        """;

    private const string Lock = """
        SELECT s.SettlementId, s.AuctionId, s.BuyerId, s.SellerId, i.Title
        FROM pay.Settlements s WITH (UPDLOCK, HOLDLOCK)
        JOIN catalog.Auctions a ON a.AuctionId = s.AuctionId
        JOIN catalog.Items i ON i.ItemId = a.ItemId
        WHERE s.SettlementId = @Id AND s.Status = 0 AND s.PaymentDeadline < SYSUTCDATETIME();
        """;

    private const string MarkDefaulted = """
        UPDATE pay.Settlements SET Status = 5, UpdatedAt = SYSUTCDATETIME() WHERE SettlementId = @SettlementId;
        UPDATE catalog.Auctions SET Outcome = 2, UpdatedAt = SYSUTCDATETIME() WHERE AuctionId = @AuctionId;
        IF NOT EXISTS (SELECT 1 FROM ident.UserStrikes WHERE SettlementId = @SettlementId)
            INSERT ident.UserStrikes (UserId, SettlementId, Reason, CreatedAt) VALUES (@BuyerId, @SettlementId, 'NonPayment', SYSUTCDATETIME());
        SELECT COUNT(*) FROM ident.UserStrikes WHERE UserId = @BuyerId AND ClearedAt IS NULL;
        SELECT Status, StatusVersion FROM ident.Users WITH (UPDLOCK) WHERE UserId = @BuyerId;
        """;

    private const string Suspend = """
        UPDATE ident.Users SET Status = 2, SuspendedUntil = NULL, StatusVersion = StatusVersion + 1, UpdatedAt = SYSUTCDATETIME()
        WHERE UserId = @BuyerId;
        UPDATE ident.RefreshTokens SET RevokedAt = SYSUTCDATETIME() WHERE UserId = @BuyerId AND RevokedAt IS NULL;
        INSERT ops.AuditLog (ActorId, Action, TargetType, TargetId, Before, After, Reason, CreatedAt)
        VALUES (NULL, 'user.suspend.strikes', 'User', @Target, @Before, @After, @Reason, SYSUTCDATETIME());
        SELECT StatusVersion FROM ident.Users WHERE UserId = @BuyerId;
        """;

    private const string AuditAndOutbox = """
        INSERT ops.AuditLog (ActorId, Action, TargetType, TargetId, After, CreatedAt)
        VALUES (NULL, 'settlement.defaulted', 'Settlement', @Target, @After, SYSUTCDATETIME());
        INSERT notify.Outbox (Topic, [Key], Payload, CreatedAt) VALUES (@Topic, @Target, @Payload, SYSUTCDATETIME());
        """;

    /// <summary>The highest other bidder who could take a second-chance offer (spec 10 R6.1, R6.2): active, at their highest bid.</summary>
    public const string NextBidderSql = """
        SELECT TOP 1 b.UserId, MAX(b.Amount) AS Amount
        FROM bidding.Bids b
        JOIN ident.Users u ON u.UserId = b.UserId
        WHERE b.AuctionId = @AuctionId AND b.UserId <> @BuyerId AND b.VoidedAt IS NULL AND u.Status = 1
        GROUP BY b.UserId
        ORDER BY MAX(b.Amount) DESC, MIN(b.ESeq);
        """;

    private sealed class Row
    {
        public Guid SettlementId { get; set; }
        public Guid AuctionId { get; set; }
        public Guid BuyerId { get; set; }
        public Guid SellerId { get; set; }
        public string Title { get; set; } = "";
    }

    private sealed class UserRow
    {
        public byte Status { get; set; }
        public int StatusVersion { get; set; }
    }

    private sealed class Next
    {
        public Guid UserId { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>One pass. Returns what was defaulted. Throws StaleFencingTokenException when another copy leads.</summary>
    public async Task<IReadOnlyList<DefaultResult>> RunAsync(LeaseGrant grant, int take = 100, CancellationToken ct = default)
    {
        List<Guid> due;
        await using (var connection = connections.Create())
        {
            due = (await connection.QueryAsync<Guid>(new CommandDefinition(Due, new { Take = take }, cancellationToken: ct))).ToList();
        }

        var results = new List<DefaultResult>();
        foreach (var id in due)
        {
            var result = await DefaultOneAsync(grant, id, ct);
            if (result != null)
            {
                results.Add(result);
            }
        }

        return results;
    }

    private const string ExpireSql = """
        UPDATE pay.SecondChanceOffers SET Status = 3, RespondedAt = SYSUTCDATETIME()
        OUTPUT inserted.OfferId, inserted.AuctionId, inserted.SellerId, inserted.BidderId, inserted.Amount
        WHERE Status = 0 AND ExpiresAt < SYSUTCDATETIME();
        """;

    private sealed class ExpiredRow
    {
        public Guid OfferId { get; set; }
        public Guid AuctionId { get; set; }
        public Guid SellerId { get; set; }
        public Guid BidderId { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>Open second-chance offers past 48 hours become Expired, and the seller is told (spec 10 R6.5).</summary>
    public async Task<int> ExpireOffersAsync(LeaseGrant grant, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await Fencing.EnsureAsync(connection, transaction, grant, ct);

        var rows = (await connection.QueryAsync<ExpiredRow>(new CommandDefinition(ExpireSql, transaction: transaction, cancellationToken: ct))).ToList();
        foreach (var r in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT notify.Outbox (Topic, [Key], Payload, CreatedAt) VALUES (@Topic, @Key, @Payload, SYSUTCDATETIME());",
                new
                {
                    Topic = SecondChanceTopics.Expired,
                    Key = r.OfferId.ToString(),
                    Payload = JsonSerializer.Serialize(new { offerId = r.OfferId, auctionId = r.AuctionId, sellerId = r.SellerId, bidderId = r.BidderId, amount = r.Amount, status = "Expired" }, Json),
                }, transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
        if (rows.Count > 0)
        {
            PaymentMetrics.SecondChance.Add(rows.Count, new KeyValuePair<string, object?>("result", "expired"));
        }

        return rows.Count;
    }

    private async Task<DefaultResult?> DefaultOneAsync(LeaseGrant grant, Guid settlementId, CancellationToken ct)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await Fencing.EnsureAsync(connection, transaction, grant, ct);

        var s = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(Lock, new { Id = settlementId }, transaction, cancellationToken: ct));
        if (s == null)
        {
            await transaction.CommitAsync(ct);   // paid or defaulted meanwhile
            return null;
        }

        int active;
        UserRow? user;
        using (var grid = await connection.QueryMultipleAsync(new CommandDefinition(MarkDefaulted, new { s.SettlementId, s.AuctionId, s.BuyerId }, transaction, cancellationToken: ct)))
        {
            active = await grid.ReadSingleAsync<int>();
            user = await grid.ReadSingleOrDefaultAsync<UserRow>();
        }

        var suspend = active >= StrikesToSuspend && user != null
                      && (UserStatus)user.Status is not (UserStatus.Suspended or UserStatus.Banned);
        int? newVersion = null;
        if (suspend)
        {
            newVersion = await connection.QuerySingleAsync<int>(new CommandDefinition(Suspend, new
            {
                s.BuyerId,
                Target = s.BuyerId.ToString(),
                Before = JsonSerializer.Serialize(new { status = ((UserStatus)user!.Status).ToString(), sv = user.StatusVersion }, Json),
                After = JsonSerializer.Serialize(new { status = nameof(UserStatus.Suspended), until = (DateTime?)null, strikes = active }, Json),
                Reason = $"{active} unpaid wins (last: settlement {s.SettlementId})",
            }, transaction, cancellationToken: ct));
        }

        var next = await connection.QuerySingleOrDefaultAsync<Next>(new CommandDefinition(NextBidderSql, new { s.AuctionId, s.BuyerId }, transaction, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(AuditAndOutbox, new
        {
            Target = s.SettlementId.ToString(),
            After = JsonSerializer.Serialize(new { buyerId = s.BuyerId, activeStrikes = active, suspended = suspend }, Json),
            Topic = DefaultedTopic,
            Payload = JsonSerializer.Serialize(new
            {
                settlementId = s.SettlementId,
                auctionId = s.AuctionId,
                buyerId = s.BuyerId,
                sellerId = s.SellerId,
                title = s.Title,
                activeStrikes = active,
                suspended = active >= StrikesToSuspend,
                canOfferSecondChance = next != null,
            }, Json),
        }, transaction, cancellationToken: ct));

        await transaction.CommitAsync(ct);

        PaymentMetrics.Defaulted.Add(1);
        PaymentMetrics.Strikes.Add(1);
        log.LogWarning("Settlement {SettlementId} defaulted: buyer {BuyerId} now has {Strikes} active strikes{Suspended}",
            s.SettlementId, s.BuyerId, active, suspend ? " and is suspended" : "");

        if (newVersion != null)
        {
            // Bids and sign-in see the suspension within a second (R5.4), as for an admin suspension.
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<UserStatusCache>().SetAsync(s.BuyerId, new UserStatusInfo(UserStatus.Suspended, newVersion.Value));
        }

        return new DefaultResult(s.SettlementId, s.BuyerId, active, suspend, next?.UserId);
    }
}
