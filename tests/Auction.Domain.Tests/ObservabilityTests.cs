using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Auction.Api.Admin;
using Auction.Contracts.Hosting;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Live;
using Auction.Infrastructure.Notify;
using Auction.Infrastructure.Observability;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 09 without Docker: log masking, metric names, trace links, alert rules and the Grafana board.</summary>
public class ObservabilityTests
{
    // ---------- task 2: masking ----------

    private sealed record LoginRequest(string UserName, string Password, string Email);

    private sealed record TokenReply(string Token, string AccessToken, int Count);

    private sealed record Plain(string Name, int Count);

    private sealed class Collect : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static string Logged(object value)
    {
        var sink = new Collect();
        using var log = new LoggerConfiguration().Destructure.With<SensitiveDataDestructuringPolicy>().WriteTo.Sink(sink).CreateLogger();
        log.Information("Got {@Value}", value);
        return sink.Events.Single().Properties["Value"].ToString();
    }

    [Fact]
    public void Password_is_dropped_and_email_is_masked()
    {
        var text = Logged(new LoginRequest("justin", "hunter2", "justin@example.com"));

        text.Should().Contain("justin").And.Contain("j***@example.com");
        text.Should().NotContain("hunter2").And.NotContain("Password").And.NotContain("justin@example.com");
    }

    [Fact]
    public void Token_and_access_token_are_dropped()
    {
        var text = Logged(new TokenReply("secret-1", "secret-2", 5));

        text.Should().NotContain("secret-1").And.NotContain("secret-2").And.Contain("5");
    }

    [Fact]
    public void Objects_without_secrets_log_as_usual()
    {
        Logged(new Plain("lamp", 3)).Should().Contain("lamp").And.Contain("Count");
    }

    [Theory]
    [InlineData("justin@example.com", "j***@example.com")]
    [InlineData("a@b.co", "a***@b.co")]
    [InlineData("not-an-email", "***")]
    [InlineData("@x.com", "***")]
    public void Email_mask(string input, string expected) =>
        SensitiveDataDestructuringPolicy.MaskEmail(input).Should().Be(expected);

    // ---------- task 3: instruments ----------

    /// <summary>The names R2 asks for, minus the Web ones (checked in the Web tests) and the built-in grpc_server_*.</summary>
    public static readonly string[] ServiceInstruments =
    [
        "bids_total", "bid_rejections_total", "bid_latency_ms", "persist_lag_ms", "persist_batch_size", "persist_dlq_total",
        "settlement_lag_seconds", "lifecycle_is_leader", "live_read_delay_ms", "notifications_created_total",
        "redis_replica_connected", "redis_memory_used_bytes",
    ];

    [Fact]
    public void Every_instrument_in_the_spec_exists_with_its_exact_name_and_no_unit()
    {
        foreach (var type in new[] { typeof(BiddingMetrics), typeof(PersisterMetrics), typeof(LifecycleMetrics), typeof(LiveMetrics), typeof(NotifyMetrics), typeof(RedisMetrics) })
        {
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        }

        var seen = new Dictionary<string, Instrument>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name.StartsWith("Auction.", StringComparison.Ordinal))
            {
                seen[instrument.Name] = instrument;
            }
        };
        listener.Start();

        seen.Keys.Should().Contain(ServiceInstruments);
        foreach (var name in ServiceInstruments)
        {
            seen[name].Unit.Should().BeNull($"a unit would rename {name} in Prometheus");
        }
    }

    [Fact]
    public void A_rejected_bid_counts_in_both_counters()
    {
        long rejected = 0, codeSeen = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BiddingMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "bids_total" && tags.ToArray().Any(t => t.Key == "result" && (string?)t.Value == "rejected"))
            {
                Interlocked.Add(ref rejected, value);
            }

            if (instrument.Name == "bid_rejections_total" && tags.ToArray().Any(t => t.Key == "code"))
            {
                Interlocked.Add(ref codeSeen, value);
            }
        });
        listener.Start();

        BiddingMetrics.Bids.Add(1, new KeyValuePair<string, object?>("result", "rejected"));
        BiddingMetrics.Rejections.Add(1, new KeyValuePair<string, object?>("code", "BELOW_MINIMUM"));

        rejected.Should().Be(1);
        codeSeen.Should().Be(1);
    }

    // ---------- task 4: trace through the stream ----------

    [Fact]
    public void Trace_id_correlation_becomes_a_span_link()
    {
        var trace = ActivityTraceId.CreateRandom().ToHexString();

        PersistPlannerLink(trace, out var link).Should().BeTrue();
        link.Context.TraceId.ToHexString().Should().Be(trace);
    }

    [Theory]
    [InlineData("c")]
    [InlineData("my-own-correlation-id")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public void Other_correlation_ids_make_no_link(string corr) => PersistPlannerLink(corr, out _).Should().BeFalse();

    private static bool PersistPlannerLink(string corr, out ActivityLink link) => PersistTracing.TryLink(corr, out link);

    [Fact]
    public void Persister_starts_a_consumer_span_linked_to_each_bid_trace()
    {
        var trace = ActivityTraceId.CreateRandom().ToHexString();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PersistTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var entry = new PersistEntry("1-0", PersistEntryType.Bid, Guid.NewGuid(), 1, 1, 1, Corr: trace);
        using var span = PersistTracing.StartBatch([entry, entry with { Corr = "c" }]);

        span.Should().NotBeNull();
        span!.Kind.Should().Be(ActivityKind.Consumer);
        span.Links.Select(l => l.Context.TraceId.ToHexString()).Should().Equal(trace);
    }

    [Fact]
    public void Persist_entries_carry_the_corr_field()
    {
        var fields = new Dictionary<string, string>
        {
            ["type"] = "State", ["auction"] = Guid.NewGuid().ToString(), ["eseq"] = "1", ["seq"] = "-", ["at"] = "1",
            ["status"] = "Frozen", ["corr"] = "abc",
        };

        PersistEntryParser.Parse("1-0", fields).Corr.Should().Be("abc");
        fields.Remove("corr");
        PersistEntryParser.Parse("1-0", fields).Corr.Should().BeNull();
    }

    // ---------- tasks 5 and 6: alert rules and the Grafana board ----------

    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Auction.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Auction.sln not found above the test folder.");
    }

    /// <summary>Everything a rule or panel may read: R2 names, the Web ones, built-ins and Prometheus's own.</summary>
    private static readonly string[] KnownMetrics =
    [
        .. ServiceInstruments, "live_fanout_delay_ms", "live_streams_open", "circuits_active", "up",
        "persisted_entries_total", "auctions_settled_total",
    ];

    private static IEnumerable<string> MetricsIn(string expr) =>
        Regex.Matches(expr, @"\b([a-z][a-z0-9_]*)(?=\s*[\{\[\)\s]|$)").Select(m => m.Groups[1].Value)
            .Select(n => Regex.Replace(n, "_(bucket|sum|count)$", ""))
            .Where(n => n.Contains('_') || n == "up")
            .Where(n => !new[] { "histogram_quantile", "sum_by", "by" }.Contains(n));

    [Fact]
    public void Rules_file_has_all_eight_alerts_with_known_metrics()
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "deploy", "prometheus", "rules.yml"));
        var names = Regex.Matches(text, @"- alert:\s*(\w+)").Select(m => m.Groups[1].Value).ToList();

        names.Should().BeEquivalentTo("ApiDown", "BidErrorRate", "BidLatency", "PersistLag", "PersistDLQ", "SettlementLag", "RedisReplicaDown", "NoLifecycleLeader");

        foreach (Match m in Regex.Matches(text, @"expr:\s*(.+)"))
        {
            MetricsIn(m.Groups[1].Value).Should().OnlyContain(n => KnownMetrics.Contains(n), m.Groups[1].Value);
        }

        text.Should().Contain("bid_latency_ms_bucket").And.Contain("> 350").And.Contain("for: 5m");
        text.Should().Contain("> 5000").And.Contain("> 30");
    }

    [Fact]
    public void Prometheus_loads_the_rules_and_the_compose_file_mounts_them()
    {
        var root = Repo();
        File.ReadAllText(Path.Combine(root, "deploy", "prometheus", "prometheus.yml")).Should().Contain("rule_files").And.Contain("/etc/prometheus/rules.yml");
        var compose = File.ReadAllText(Path.Combine(root, "deploy", "compose.yml"));
        compose.Should().Contain("rules.yml:/etc/prometheus/rules.yml").And.Contain("grafana/dashboards:/var/lib/grafana/dashboards");
    }

    [Fact]
    public void Grafana_board_has_eight_panels_reading_known_metrics()
    {
        var root = Repo();
        var board = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "deploy", "grafana", "dashboards", "auction-mvp.json"))).RootElement;

        board.GetProperty("title").GetString().Should().Be("Auction MVP");
        var panels = board.GetProperty("panels").EnumerateArray().ToList();
        panels.Should().HaveCount(8);
        panels.Select(p => p.GetProperty("title").GetString()).Should().OnlyHaveUniqueItems();

        foreach (var panel in panels)
        {
            panel.GetProperty("datasource").GetProperty("uid").GetString().Should().Be("prometheus");
            foreach (var target in panel.GetProperty("targets").EnumerateArray())
            {
                MetricsIn(target.GetProperty("expr").GetString()!).Should().NotBeEmpty().And.OnlyContain(n => KnownMetrics.Contains(n));
            }
        }

        File.ReadAllText(Path.Combine(root, "deploy", "grafana", "provisioning", "datasources", "prometheus.yml")).Should().Contain("uid: prometheus");
        File.ReadAllText(Path.Combine(root, "deploy", "grafana", "provisioning", "dashboards", "dashboards.yml")).Should().Contain("/var/lib/grafana/dashboards");
    }

    [Fact]
    public void Admin_page_error_query_uses_the_result_tag()
    {
        SystemStatsReader.Queries["errors"].Should().Contain("result=\"error\"").And.NotContain("outcome");
    }
}