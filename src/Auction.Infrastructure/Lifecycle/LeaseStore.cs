using Auction.Infrastructure.Bidding.Persistence;
using Dapper;

namespace Auction.Infrastructure.Lifecycle;

/// <summary>Where the lease row lives. The SQL one is real; tests use a fake.</summary>
public interface ILeaseStore
{
    /// <summary>Takes or renews the lease. Returns the fencing token, or null when another holder has it.</summary>
    Task<long?> TryAcquireAsync(string leaseName, string holder, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Lets the lease expire now (on a clean stop), so another copy can take it at once.</summary>
    Task ReleaseAsync(string leaseName, string holder, CancellationToken ct = default);
}

/// <summary>The lease in bidding.LifecycleLeases (spec 05 design 3).</summary>
public sealed class SqlLeaseStore(SqlConnectionFactory connections) : ILeaseStore
{
    // One statement: renew if we hold it, take it if it expired. A new holder gets token + 1.
    private const string Acquire = """
        UPDATE bidding.LifecycleLeases WITH (UPDLOCK, HOLDLOCK)
        SET Holder = @Holder,
            FencingToken = CASE WHEN Holder = @Holder THEN FencingToken ELSE FencingToken + 1 END,
            ExpiresAt = DATEADD(MILLISECOND, @TtlMs, SYSUTCDATETIME())
        OUTPUT inserted.FencingToken
        WHERE LeaseName = @LeaseName AND (Holder = @Holder OR ExpiresAt < SYSUTCDATETIME());
        """;

    // Keeps Holder, so whoever takes it next still gets token + 1.
    private const string Release = """
        UPDATE bidding.LifecycleLeases SET ExpiresAt = '2000-01-01'
        WHERE LeaseName = @LeaseName AND Holder = @Holder;
        """;

    public async Task<long?> TryAcquireAsync(string leaseName, string holder, TimeSpan ttl, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        return await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            Acquire, new { LeaseName = leaseName, Holder = holder, TtlMs = (int)ttl.TotalMilliseconds }, cancellationToken: ct));
    }

    public async Task ReleaseAsync(string leaseName, string holder, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(Release, new { LeaseName = leaseName, Holder = holder }, cancellationToken: ct));
    }
}
