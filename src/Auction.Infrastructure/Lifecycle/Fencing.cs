using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Auction.Infrastructure.Lifecycle;

/// <summary>Who held the lease, and with which token, when a piece of leader work started.</summary>
public sealed record LeaseGrant(string LeaseName, string Holder, long Token);

/// <summary>Thrown when a leader write runs with a token that is no longer the current one (R1.3).</summary>
public sealed class StaleFencingTokenException(LeaseGrant grant)
    : Exception($"Fencing token {grant.Token} for '{grant.Holder}' is no longer current.")
{
    public LeaseGrant Grant { get; } = grant;
}

/// <summary>
/// The fencing check every leader SQL write runs first, inside its own transaction (spec 05 design 3).
/// It locks the lease row, so a new leader cannot take over until this transaction ends.
/// </summary>
public static class Fencing
{
    public const int ErrorNumber = 50001;

    public const string CheckSql = """
        IF NOT EXISTS (SELECT 1 FROM bidding.LifecycleLeases WITH (UPDLOCK)
                       WHERE LeaseName = @LeaseName AND Holder = @Holder AND FencingToken = @Token
                         AND ExpiresAt > SYSUTCDATETIME())
            THROW 50001, 'STALE_FENCING_TOKEN', 1;
        """;

    /// <summary>Runs the check in the given transaction. Throws StaleFencingTokenException when the token is stale.</summary>
    public static async Task EnsureAsync(SqlConnection connection, IDbTransaction transaction, LeaseGrant grant, CancellationToken ct = default)
    {
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                CheckSql, new { grant.LeaseName, grant.Holder, grant.Token }, transaction, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == ErrorNumber)
        {
            throw new StaleFencingTokenException(grant);
        }
    }
}
