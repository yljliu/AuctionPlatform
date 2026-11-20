using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Auction.Infrastructure.Bidding.Persistence;

/// <summary>Opens SQL connections for code that uses Dapper directly (the persister hot path).</summary>
public sealed class SqlConnectionFactory(string connectionString)
{
    public SqlConnection Create() => new(connectionString);
}

/// <summary>
/// Writes one persist plan in one transaction (spec 03 design 6). Every insert skips rows that
/// already exist, so writing the same entries twice (a crash before XACK) adds nothing (R8.3).
/// </summary>
public sealed class PersistSqlWriter(SqlConnectionFactory connections)
{
    private const string InsertBid = """
        INSERT bidding.Bids (AuctionId, UserId, ESeq, Idx, Seq, Kind, Amount, MaxAmount, IdempotencyKey, AcceptedAt)
        SELECT @AuctionId, @UserId, @ESeq, @Idx, @Seq, @Kind, @Amount, @MaxAmount, @IdempotencyKey, @AcceptedAt
        WHERE NOT EXISTS (SELECT 1 FROM bidding.Bids WITH (UPDLOCK, HOLDLOCK)
                          WHERE AuctionId = @AuctionId AND ESeq = @ESeq AND Idx = @Idx);
        """;

    private const string InsertEvent = """
        INSERT bidding.AuctionEvents (AuctionId, Seq, ESeq, EventType, Payload, OccurredAt)
        SELECT @AuctionId, @Seq, @ESeq, @EventType, @Payload, @OccurredAt
        WHERE NOT EXISTS (SELECT 1 FROM bidding.AuctionEvents WITH (UPDLOCK, HOLDLOCK)
                          WHERE AuctionId = @AuctionId AND Seq = @Seq AND EventType = @EventType);
        """;

    // Only moves forward: a replayed or late batch with an older eseq changes nothing.
    private const string UpdateAuction = """
        UPDATE catalog.Auctions SET
            CurrentPrice    = CASE WHEN @HasBid = 1 THEN @CurrentPrice ELSE CurrentPrice END,
            CurrentLeaderId = CASE WHEN @HasBid = 1 THEN @CurrentLeaderId ELSE CurrentLeaderId END,
            BidCount        = CASE WHEN @HasBid = 1 THEN @BidCount ELSE BidCount END,
            -- Redis keeps times in whole milliseconds; never let the end fall below the scheduled end (CK_Times)
            -- when SQL holds a finer time (an auction made before times were cut to milliseconds).
            EndTime         = CASE WHEN @HasBid = 1 THEN (CASE WHEN @EndTime < ScheduledEndTime THEN ScheduledEndTime ELSE @EndTime END) ELSE EndTime END,
            LastSeq         = CASE WHEN @LastSeq > LastSeq THEN @LastSeq ELSE LastSeq END,
            LastESeq        = @LastESeq,
            UpdatedAt       = SYSUTCDATETIME()
        WHERE AuctionId = @AuctionId AND LastESeq < @LastESeq;

        -- First bid locks the item's title, description, condition and category (R9.1).
        UPDATE i SET LockedAt = SYSUTCDATETIME()
        FROM catalog.Items i JOIN catalog.Auctions a ON a.ItemId = i.ItemId
        WHERE a.AuctionId = @AuctionId AND @HasBid = 1 AND i.LockedAt IS NULL;
        """;

    public async Task WriteAsync(PersistPlan plan, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        if (plan.Bids.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(InsertBid, plan.Bids.Select(b => new
            {
                b.AuctionId, b.UserId, b.ESeq, Idx = (int)b.Idx, b.Seq, Kind = (byte)b.Kind, b.Amount, b.MaxAmount, b.IdempotencyKey, b.AcceptedAt,
            }), transaction, cancellationToken: ct));
        }

        if (plan.Events.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(InsertEvent, plan.Events, transaction, cancellationToken: ct));
        }

        if (plan.Auctions.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(UpdateAuction, plan.Auctions, transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>True when SQL answers. Used to tell "SQL is down" (wait) from "this entry is bad" (count it).</summary>
    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(ct);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
            return true;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            return false;
        }
    }
}
