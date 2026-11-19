using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Infrastructure.Bidding.Persistence;
using Dapper;

namespace Auction.Infrastructure.Lifecycle;

/// <summary>A Scheduled auction about to start, with what load.lua needs.</summary>
public sealed record ScheduledAuctionRow(
    Guid AuctionId,
    Guid SellerId,
    string Currency,
    DateTime StartTime,
    DateTime EndTime,
    decimal StartingPrice,
    decimal? ReservePrice,
    int IncrementTableId,
    int SnipeWindowSec,
    int ExtensionSec,
    int MaxTotalExtensionSec,
    int Watchers = 0)
{
    public AuctionLoad ToLoad(string incrementTable) => new(
        SellerId,
        StartMs: new DateTimeOffset(DateTime.SpecifyKind(StartTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        EndMs: new DateTimeOffset(DateTime.SpecifyKind(EndTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        StartC: IncrementTable.ToCents(StartingPrice),
        ReserveC: ReservePrice == null ? null : IncrementTable.ToCents(ReservePrice.Value),
        SnipeMs: SnipeWindowSec * 1000L,
        ExtMs: ExtensionSec * 1000L,
        MaxExtMs: MaxTotalExtensionSec * 1000L,
        incrementTable,
        Currency,
        Watchers);
}

/// <summary>A Live, Frozen or Sealed auction, for recovery on leader start (spec 05 design 7).</summary>
public sealed class OpenAuctionRow
{
    public Guid AuctionId { get; set; }
    public Guid SellerId { get; set; }
    public string Currency { get; set; } = "USD";
    public byte Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime ScheduledEndTime { get; set; }
    public DateTime EndTime { get; set; }
    public decimal StartingPrice { get; set; }
    public decimal? ReservePrice { get; set; }
    public int IncrementTableId { get; set; }
    public int SnipeWindowSec { get; set; }
    public int ExtensionSec { get; set; }
    public int MaxTotalExtensionSec { get; set; }
    public decimal? CurrentPrice { get; set; }
    public Guid? CurrentLeaderId { get; set; }
    public int BidCount { get; set; }
    public long LastSeq { get; set; }
    public long LastESeq { get; set; }
    public int Watchers { get; set; }

    public AuctionStatus AuctionStatus => (AuctionStatus)Status;

    /// <summary>rebuild.lua values from SQL; maxByUser comes from bidding.Bids.</summary>
    public AuctionRebuild ToRebuild(string incrementTable, IReadOnlyDictionary<Guid, long> maxByUser)
    {
        var load = new ScheduledAuctionRow(AuctionId, SellerId, Currency, StartTime, EndTime, StartingPrice, ReservePrice,
            IncrementTableId, SnipeWindowSec, ExtensionSec, MaxTotalExtensionSec).ToLoad(incrementTable);
        var schedEndMs = Ms(ScheduledEndTime);
        var priceC = CurrentPrice == null ? 0 : IncrementTable.ToCents(CurrentPrice.Value);
        var leaderMaxC = CurrentLeaderId is Guid leader && maxByUser.TryGetValue(leader, out var max) ? Math.Max(max, priceC) : priceC;
        return new AuctionRebuild(load, schedEndMs, ExtUsedMs: Math.Max(0, load.EndMs - schedEndMs), priceC, CurrentLeaderId,
            leaderMaxC, BidCount, LastSeq, LastESeq, maxByUser);
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}

/// <summary>The SQL the lifecycle loop needs (spec 05 design 4). Every write is fenced (R1.3).</summary>
public sealed class LifecycleSql(SqlConnectionFactory connections)
{
    private const string Scheduled = """
        SELECT TOP (@Limit) AuctionId, SellerId, Currency, StartTime, EndTime, StartingPrice, ReservePrice,
               IncrementTableId, SnipeWindowSec, ExtensionSec, MaxTotalExtensionSec,
               (SELECT COUNT(*) FROM bidding.Watchlist w WHERE w.AuctionId = catalog.Auctions.AuctionId) AS Watchers
        FROM catalog.Auctions
        WHERE Status = 1 AND StartTime <= @Until
        ORDER BY StartTime;
        """;

    private const string Tiers = "SELECT FromPrice, Increment FROM catalog.BidIncrementTiers WHERE TableId = @TableId;";

    private const string SetStatus = """
        UPDATE catalog.Auctions SET Status = @To, UpdatedAt = SYSUTCDATETIME()
        WHERE AuctionId = @AuctionId AND Status = @From;
        """;

    private const string Open = """
        SELECT AuctionId, SellerId, Currency, Status, StartTime, ScheduledEndTime, EndTime, StartingPrice, ReservePrice,
               IncrementTableId, SnipeWindowSec, ExtensionSec, MaxTotalExtensionSec, CurrentPrice, CurrentLeaderId,
               BidCount, LastSeq, LastESeq,
               (SELECT COUNT(*) FROM bidding.Watchlist w WHERE w.AuctionId = catalog.Auctions.AuctionId) AS Watchers
        FROM catalog.Auctions
        WHERE Status IN (2, 3, 7);
        """;

    // Maxes only go up, so the highest one per bidder is the latest (spec 05 design 7).
    private const string MaxByUser = """
        SELECT UserId, MAX(MaxAmount) AS MaxAmount FROM bidding.Bids
        WHERE AuctionId = @AuctionId AND MaxAmount IS NOT NULL
        GROUP BY UserId;
        """;

    /// <summary>Every Live, Sealed and Frozen auction.</summary>
    public async Task<IReadOnlyList<OpenAuctionRow>> OpenAuctionsAsync(CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        return (await connection.QueryAsync<OpenAuctionRow>(new CommandDefinition(Open, cancellationToken: ct))).ToList();
    }

    /// <summary>Each bidder's highest max, in cents (for the private max hash).</summary>
    public async Task<IReadOnlyDictionary<Guid, long>> MaxByUserAsync(Guid auctionId, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<(Guid UserId, decimal MaxAmount)>(new CommandDefinition(
            MaxByUser, new { AuctionId = auctionId }, cancellationToken: ct));
        return rows.ToDictionary(r => r.UserId, r => IncrementTable.ToCents(r.MaxAmount));
    }

    /// <summary>Scheduled auctions starting at or before until (UTC), oldest start first.</summary>
    public async Task<IReadOnlyList<ScheduledAuctionRow>> ScheduledStartingByAsync(DateTime until, int limit, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<ScheduledAuctionRow>(new CommandDefinition(
            Scheduled, new { Until = until, Limit = limit }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>The increment table in the Redis "inc" form, or null when it has no valid tiers.</summary>
    public async Task<string?> IncrementTableAsync(int tableId, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        var rows = (await connection.QueryAsync<(decimal FromPrice, decimal Increment)>(new CommandDefinition(
            Tiers, new { TableId = tableId }, cancellationToken: ct))).ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        try
        {
            return IncrementTable.FromMoney(rows).ToRedisString();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves the SQL status from one value to the next, inside a fenced transaction.
    /// Returns false when the row was not in the from status (already moved). Throws StaleFencingTokenException.
    /// </summary>
    public async Task<bool> SetStatusAsync(LeaseGrant grant, Guid auctionId, AuctionStatus from, AuctionStatus to, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();
        await Fencing.EnsureAsync(connection, transaction, grant, ct);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            SetStatus, new { AuctionId = auctionId, From = (byte)from, To = (byte)to }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return changed == 1;
    }
}
