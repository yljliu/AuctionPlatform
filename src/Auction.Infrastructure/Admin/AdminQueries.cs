using System.Diagnostics;
using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Role = Auction.Domain.Enums.Role;

namespace Auction.Infrastructure.Admin;

public sealed record AdminUserRow(
    Guid UserId,
    string Username,
    string Email,
    UserStatus Status,
    DateTime? SuspendedUntil,
    IReadOnlyList<Role> Roles,
    DateTime CreatedAt);

public sealed record AdminStatsView(
    int LiveAuctions,
    int ScheduledAuctions,
    int SealedAuctions,
    int FrozenAuctions,
    int Users,
    IReadOnlyList<int> BidsPerMinute,
    bool SqlOk,
    bool RedisOk,
    long RedisLatencyMs,
    string LifecycleLeader)
{
    public int BidsLast15Min => BidsPerMinute.Sum();
}

/// <summary>Reads for the admin pages (spec 06 task 9). Every value is a parameter.</summary>
public sealed class AdminQueries(AuctionDbContext db, IConnectionMultiplexer redis, TimeProvider clock)
{
    public const int StatsMinutes = 15;

    /// <summary>Users whose username or email contains the text, newest first. Empty text = everyone.</summary>
    public async Task<(IReadOnlyList<AdminUserRow> Users, int Total)> SearchUsersAsync(string? text, Paging paging, CancellationToken ct = default)
    {
        var query = db.Users.AsNoTracking().Where(u => !u.IsBot);
        text = (text ?? "").Trim();
        if (text.Length > 0)
        {
            var pattern = SearchText.ContainsPattern(text);
            query = query.Where(u => EF.Functions.Like(u.Username, pattern) || EF.Functions.Like(u.Email, pattern));
        }

        var total = await query.CountAsync(ct);
        var users = await query.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.UserId)
            .Skip(paging.Skip).Take(paging.Size)
            .Select(u => new AdminUserRow(u.UserId, u.Username, u.Email, u.Status, u.SuspendedUntil,
                u.Roles.Select(r => r.Role).ToList(), u.CreatedAt))
            .ToListAsync(ct);
        return (users, total);
    }

    /// <summary>Tile numbers, bids per minute for the chart, and a quick health check of SQL and Redis.</summary>
    public async Task<AdminStatsView> GetStatsAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var from = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes(-(StatsMinutes - 1));

        var counts = new Dictionary<byte, int>();
        var perMinute = new int[StatsMinutes];
        var users = 0;
        var leader = "";
        var sqlOk = true;
        try
        {
            var connection = db.Database.GetDbConnection();
            using var results = await connection.QueryMultipleAsync(new CommandDefinition("""
                SELECT Status, COUNT(*) AS N FROM catalog.Auctions WHERE Status IN (1, 2, 3, 7) GROUP BY Status;
                SELECT COUNT(*) FROM ident.Users WHERE IsBot = 0;
                SELECT DATEDIFF(MINUTE, @From, AcceptedAt) AS M, COUNT(*) AS N
                FROM bidding.Bids WHERE AcceptedAt >= @From AND Kind <> 3
                GROUP BY DATEDIFF(MINUTE, @From, AcceptedAt);
                SELECT Holder FROM bidding.LifecycleLeases WHERE LeaseName = 'lifecycle' AND ExpiresAt > SYSUTCDATETIME();
                """, new { From = from }, cancellationToken: ct));
            foreach (var (status, n) in await results.ReadAsync<(byte, int)>())
            {
                counts[status] = n;
            }

            users = await results.ReadSingleAsync<int>();
            foreach (var (minute, n) in await results.ReadAsync<(int, int)>())
            {
                if (minute is >= 0 and < StatsMinutes)
                {
                    perMinute[minute] = n;
                }
            }

            leader = (await results.ReadFirstOrDefaultAsync<string>())?.Split(':')[0] ?? "";
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or TimeoutException)
        {
            sqlOk = false;
        }

        var redisOk = true;
        long redisMs = 0;
        try
        {
            var watch = Stopwatch.StartNew();
            await redis.GetDatabase().PingAsync();
            redisMs = watch.ElapsedMilliseconds;
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            redisOk = false;
        }

        int Count(AuctionStatus s) => counts.GetValueOrDefault((byte)s);
        return new AdminStatsView(Count(AuctionStatus.Live), Count(AuctionStatus.Scheduled), Count(AuctionStatus.Sealed),
            Count(AuctionStatus.Frozen), users, perMinute, sqlOk, redisOk, redisMs, leader);
    }
}
