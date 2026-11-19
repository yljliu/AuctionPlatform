using System.Text.Json;
using Auction.Contracts;
using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Auction.Simulator.Sim;

/// <summary>
/// ops.SimulationRuns rows and the verifier's SQL reads (design 2). The simulator only reads business tables.
/// </summary>
public sealed class SqlRunStore(string connectionString, TimeProvider clock) : IRunStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SaveStartAsync(Run run, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT ops.SimulationRuns (RunId, Scenario, Config, StartedBy, StartedAt, Status)
            VALUES (@RunId, @Scenario, @Config, @StartedBy, @StartedAt, @Status);
            """, new
        {
            run.RunId, run.Config.Scenario, Config = JsonSerializer.Serialize(run.Config, Json), run.StartedBy,
            StartedAt = run.StartedAt.UtcDateTime, Status = (byte)SimulationStatus.Running,
        }, cancellationToken: ct));
    }

    public async Task SaveEndAsync(Run run, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE ops.SimulationRuns SET EndedAt = @EndedAt, Status = @Status, Report = @Report WHERE RunId = @RunId;
            """, new
        {
            run.RunId, EndedAt = (run.EndedAt ?? clock.GetUtcNow()).UtcDateTime,
            Status = (byte)(run.Report != null ? SimulationStatus.Completed : SimulationStatus.Aborted),
            Report = run.Report?.ToJson() ?? JsonSerializer.Serialize(new { result = "ERROR", message = run.Message }),
        }, cancellationToken: ct));
    }

    private sealed class RunRow
    {
        public Guid RunId { get; set; }
        public string Scenario { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public byte Status { get; set; }
        public string? Report { get; set; }
    }

    public async Task<IReadOnlyList<RunInfo>> ListAsync(int limit, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
            "SELECT TOP (@Limit) RunId, Scenario, StartedAt, EndedAt, Status, Report FROM ops.SimulationRuns ORDER BY StartedAt DESC;",
            new { Limit = limit }, cancellationToken: ct));
        return rows.Select(r => new RunInfo
        {
            RunId = r.RunId.ToString(), Scenario = r.Scenario,
            Status = r.Report?.Contains("\"PASS\"") == true ? "Passed" : r.Status == (byte)SimulationStatus.Running ? "Interrupted" : "Failed",
            StartedAtMs = new DateTimeOffset(DateTime.SpecifyKind(r.StartedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            EndedAtMs = r.EndedAt == null ? 0 : new DateTimeOffset(DateTime.SpecifyKind(r.EndedAt.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            ReportJson = r.Report ?? "",
        }).ToList();
    }

    private sealed class AuctionRow
    {
        public Guid AuctionId { get; set; }
        public byte Status { get; set; }
        public decimal? CurrentPrice { get; set; }
        public Guid? CurrentLeaderId { get; set; }
        public DateTime EndTime { get; set; }
        public DateTime ScheduledEndTime { get; set; }
        public int MaxTotalExtensionSec { get; set; }
        public decimal StartingPrice { get; set; }
        public int IncrementTableId { get; set; }
    }

    private sealed class BidRow
    {
        public Guid AuctionId { get; set; }
        public long ESeq { get; set; }
        public byte Idx { get; set; }
        public byte Kind { get; set; }
        public Guid UserId { get; set; }
        public decimal Amount { get; set; }
        public decimal? MaxAmount { get; set; }
        public Guid? IdempotencyKey { get; set; }
        public DateTime? VoidedAt { get; set; }
    }

    private sealed class EventRow
    {
        public Guid AuctionId { get; set; }
        public long Seq { get; set; }
        public short EventType { get; set; }   // 1 = Bid, 2 = State (PersistPlanner)
        public string Payload { get; set; } = "{}";
    }

    public async Task<IReadOnlyList<AuctionData>> LoadForVerifyAsync(IReadOnlyList<Guid> auctionIds, IReadOnlyCollection<Guid> acceptedKeys,
        TimeSpan wait, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + wait;
        while (true)
        {
            await using var connection = new SqlConnection(connectionString);
            var pending = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM catalog.Auctions
                WHERE AuctionId IN @Ids AND EndTime <= SYSUTCDATETIME() AND Status NOT IN (5, 6);
                """, new { Ids = auctionIds }, cancellationToken: ct));
            var found = acceptedKeys.Count == 0 ? 0 : await CountKeysAsync(connection, acceptedKeys, ct);
            if ((pending == 0 && found >= acceptedKeys.Count) || clock.GetUtcNow() >= deadline)
            {
                return await ReadAsync(connection, auctionIds, ct);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private static async Task<int> CountKeysAsync(SqlConnection connection, IReadOnlyCollection<Guid> keys, CancellationToken ct)
    {
        var found = 0;
        foreach (var chunk in keys.Chunk(1000))
        {
            found += await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(DISTINCT IdempotencyKey) FROM bidding.Bids WHERE IdempotencyKey IN @Keys;", new { Keys = chunk }, cancellationToken: ct));
        }

        return found;
    }

    private static async Task<IReadOnlyList<AuctionData>> ReadAsync(SqlConnection connection, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        using var results = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT AuctionId, Status, CurrentPrice, CurrentLeaderId, EndTime, ScheduledEndTime, MaxTotalExtensionSec, StartingPrice, IncrementTableId
            FROM catalog.Auctions WHERE AuctionId IN @Ids;
            SELECT AuctionId, ESeq, Idx, Kind, UserId, Amount, MaxAmount, IdempotencyKey, VoidedAt
            FROM bidding.Bids WHERE AuctionId IN @Ids ORDER BY AuctionId, ESeq, Idx;
            SELECT AuctionId, Seq, EventType, Payload FROM bidding.AuctionEvents WHERE AuctionId IN @Ids AND Seq > 0 ORDER BY AuctionId, Seq;
            SELECT TableId, FromPrice, Increment FROM catalog.BidIncrementTiers;
            """, new { Ids = ids }, cancellationToken: ct));
        var auctions = (await results.ReadAsync<AuctionRow>()).ToList();
        var bids = (await results.ReadAsync<BidRow>()).ToLookup(b => b.AuctionId);
        var events = (await results.ReadAsync<EventRow>()).ToLookup(e => e.AuctionId);
        var tables = (await results.ReadAsync<(int TableId, decimal FromPrice, decimal Increment)>())
            .GroupBy(t => t.TableId).ToDictionary(g => g.Key, g => IncrementTable.FromMoney(g.Select(t => (t.FromPrice, t.Increment))));

        return auctions.Select(a => new AuctionData(
            a.AuctionId, (AuctionStatus)a.Status, a.CurrentPrice, a.CurrentLeaderId, a.EndTime, a.ScheduledEndTime, a.MaxTotalExtensionSec,
            a.StartingPrice, tables[a.IncrementTableId],
            bids[a.AuctionId].Select(b => new BidRowData(b.ESeq, b.Idx, (BidKind)b.Kind, b.UserId, b.Amount, b.MaxAmount, b.IdempotencyKey, b.VoidedAt != null)).ToList(),
            events[a.AuctionId].Select(e => new EventData(e.Seq, e.EventType == 1 ? "Bid" : "State", e.Payload)).ToList())).ToList();
    }
}
