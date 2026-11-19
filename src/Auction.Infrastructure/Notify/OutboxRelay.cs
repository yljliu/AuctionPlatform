using System.Data;
using Auction.Infrastructure.Bidding.Persistence;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Notify;

/// <summary>
/// Copies new notify.Outbox rows to the Redis stream "outbox" (spec 07 design 1, R4.1).
/// The rows are locked while they are sent (UPDLOCK, READPAST), so two workers copies never send the same rows.
/// A crash after XADD and before the UPDATE sends those rows again; readers drop repeats by OutboxId.
/// </summary>
public sealed class OutboxRelay(SqlConnectionFactory connections, IConnectionMultiplexer redis, ILogger<OutboxRelay> log)
{
    public const string Stream = "outbox";
    public const int BatchSize = 500;
    public const int StreamMaxLength = 100_000;

    private sealed class Row
    {
        public long OutboxId { get; set; }
        public string Topic { get; set; } = "";
        public string Key { get; set; } = "";
        public string Payload { get; set; } = "{}";
    }

    private const string Take = """
        SELECT TOP (@Batch) OutboxId, Topic, [Key], Payload
        FROM notify.Outbox WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE PublishedAt IS NULL
        ORDER BY OutboxId;
        """;

    private const string Mark = "UPDATE notify.Outbox SET PublishedAt = SYSUTCDATETIME() WHERE OutboxId IN @Ids;";

    /// <summary>The Redis stream to write to. Tests use their own name so they never meet a running workers copy.</summary>
    public string StreamKey { get; init; } = Stream;

    /// <summary>Tests only: runs after XADD and before the UPDATE (throw here to act out a crash).</summary>
    public Func<Task>? BeforeMarkForTests { get; set; }

    /// <summary>Sends one batch. Returns how many rows were sent.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(Take, new { Batch = BatchSize }, transaction, cancellationToken: ct))).ToList();
        if (rows.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }

        var db = redis.GetDatabase();
        var sends = rows.Select(r => db.StreamAddAsync(StreamKey,
        [
            new NameValueEntry("id", r.OutboxId), new NameValueEntry("topic", r.Topic),
            new NameValueEntry("key", r.Key), new NameValueEntry("payload", r.Payload),
        ], maxLength: StreamMaxLength, useApproximateMaxLength: true)).ToList();
        await Task.WhenAll(sends);

        if (BeforeMarkForTests != null)
        {
            await BeforeMarkForTests();
        }

        await connection.ExecuteAsync(new CommandDefinition(Mark, new { Ids = rows.Select(r => r.OutboxId).ToArray() }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        log.LogDebug("Outbox relay sent {Count} rows", rows.Count);
        return rows.Count;
    }
}
