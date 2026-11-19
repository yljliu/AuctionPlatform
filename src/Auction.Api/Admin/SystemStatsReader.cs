using System.Globalization;
using System.Text.Json;
using Auction.Contracts;
using Auction.Domain.Enums;
using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Auction.Api.Admin;

/// <summary>Where to find Prometheus and which /health/ready addresses to poll (config section "SystemStats").</summary>
public sealed class SystemStatsOptions
{
    /// <summary>e.g. http://prometheus:9090. Empty = no Prometheus (lite stack); those tiles show "-".</summary>
    public string PrometheusUrl { get; set; } = "";

    /// <summary>"service=url" pairs, e.g. "api=http://api-1:7080/health/ready".</summary>
    public string[] Health { get; set; } = [];
}

/// <summary>
/// AdminService.GetSystemStats (spec 08 task 7, R5.1, R5.2): Prometheus for rates and latency, Redis INFO for
/// memory and replicas, SQL for live auctions, and /health/ready polls. Cached 5 s; each part fails on its own.
/// </summary>
public sealed class SystemStatsReader(
    IHttpClientFactory http,
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopes,
    SystemStatsOptions options,
    TimeProvider clock,
    ILogger<SystemStatsReader> log)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    // Names are matched loosely because the OpenTelemetry exporter may add a unit suffix.
    public static readonly IReadOnlyDictionary<string, string> Queries = new Dictionary<string, string>
    {
        ["bids"] = "sum(rate({__name__=~\"bids_total\"}[1m]))",
        ["errors"] = "sum(rate({__name__=~\"bids_total\",result=\"error\"}[1m])) / sum(rate({__name__=~\"bids_total\"}[1m]))",
        ["p99"] = "histogram_quantile(0.99, sum by (le) (rate({__name__=~\"bid_latency_ms.*_bucket\"}[1m])))",
        ["lag"] = "max({__name__=~\"persist_lag_ms.*\"})",
        ["online"] = "sum({__name__=~\"circuits_active.*\"})",
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SystemStats? _cached;
    private DateTimeOffset _cachedAt;

    public async Task<SystemStats> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (_cached != null && now - _cachedAt < CacheFor)
            {
                return _cached;
            }

            var stats = new SystemStats
            {
                AtMs = now.ToUnixTimeMilliseconds(), BidsPerSec = -1, OnlineUsers = -1, PersistLagMs = -1, ErrorRate = -1,
                P99BidLatencyMs = -1, RedisMemoryPct = -1, RedisReplicas = -1, LiveAuctions = -1,
            };

            await Task.WhenAll(PrometheusAsync(stats, ct), RedisAsync(stats), SqlAsync(stats, ct), HealthAsync(stats, ct));
            _cached = stats;
            _cachedAt = now;
            return stats;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PrometheusAsync(SystemStats stats, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.PrometheusUrl))
        {
            return;
        }

        try
        {
            var client = http.CreateClient("stats");
            var values = await Task.WhenAll(Queries.Select(async q => (q.Key, Value: await QueryAsync(client, q.Value, ct))));
            var map = values.ToDictionary(v => v.Key, v => v.Value);
            stats.BidsPerSec = map["bids"] ?? 0;
            stats.ErrorRate = map["errors"] ?? 0;
            stats.P99BidLatencyMs = map["p99"] ?? -1;
            stats.PersistLagMs = map["lag"] ?? -1;
            stats.OnlineUsers = (int)Math.Round(map["online"] ?? -1);
            stats.PrometheusOk = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogDebug(ex, "Prometheus is not answering; system stats without it");
        }
    }

    private async Task<double?> QueryAsync(HttpClient client, string query, CancellationToken ct)
    {
        var url = $"{options.PrometheusUrl.TrimEnd('/')}/api/v1/query?query={Uri.EscapeDataString(query)}";
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return ParseScalar(await JsonDocument.ParseAsync(body, cancellationToken: ct));
    }

    /// <summary>The first sample of a Prometheus vector answer; null when empty or NaN.</summary>
    public static double? ParseScalar(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            return null;
        }

        var value = result[0].GetProperty("value")[1].GetString();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }

    private async Task RedisAsync(SystemStats stats)
    {
        try
        {
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected && !s.IsReplica);
            if (server == null)
            {
                return;
            }

            var info = (await server.InfoAsync()).SelectMany(g => g).ToDictionary(p => p.Key, p => p.Value);
            var used = long.TryParse(info.GetValueOrDefault("used_memory"), out var u) ? u : 0;
            var max = long.TryParse(info.GetValueOrDefault("maxmemory"), out var m) ? m : 0;
            stats.RedisMemoryBytes = used;
            stats.RedisMemoryPct = max > 0 ? Math.Round(100.0 * used / max, 1) : -1;
            stats.RedisReplicas = int.TryParse(info.GetValueOrDefault("connected_slaves"), out var r) ? r : 0;
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogDebug(ex, "Redis INFO failed");
        }
    }

    private async Task SqlAsync(SystemStats stats, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();
            stats.LiveAuctions = await db.Auctions.CountAsync(a => a.Status == AuctionStatus.Live, ct);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            log.LogDebug(ex, "SQL count failed");
        }
    }

    private async Task HealthAsync(SystemStats stats, CancellationToken ct)
    {
        var targets = ParseHealth(options.Health);
        if (targets.Count == 0)
        {
            return;
        }

        var client = http.CreateClient("stats");
        var checks = await Task.WhenAll(targets.Select(async t =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                using var response = await client.GetAsync(t.Url, timeout.Token);
                return (t.Service, Ok: response.IsSuccessStatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return (t.Service, Ok: false);
            }
        }));

        foreach (var group in checks.GroupBy(c => c.Service))
        {
            stats.Services.Add(new ServiceHealth { Service = group.Key, Healthy = group.Count(c => c.Ok), Total = group.Count() });
        }
    }

    /// <summary>"api=http://api-1:7080/health/ready" pairs; bad ones are skipped.</summary>
    public static IReadOnlyList<(string Service, string Url)> ParseHealth(IEnumerable<string> pairs) =>
        pairs.Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0 && Uri.TryCreate(p[1].Trim(), UriKind.Absolute, out _))
            .Select(p => (p[0].Trim(), p[1].Trim()))
            .ToList();
}
