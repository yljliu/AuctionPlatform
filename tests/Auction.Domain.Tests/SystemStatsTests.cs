using System.Text.Json;
using Auction.Api.Admin;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>Spec 08 task 7 without Prometheus or Redis: reading answers, health lists and the query set.</summary>
public class SystemStatsTests
{
    private static double? Scalar(string json) => SystemStatsReader.ParseScalar(JsonDocument.Parse(json));

    [Fact]
    public void A_prometheus_vector_gives_its_first_value() =>
        Scalar("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1700000000.123,"41.5"]}]}}""").Should().Be(41.5);

    [Theory]
    [InlineData("""{"status":"success","data":{"resultType":"vector","result":[]}}""")]
    [InlineData("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1,"NaN"]}]}}""")]
    [InlineData("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1,"+Inf"]}]}}""")]
    [InlineData("""{"status":"error"}""")]
    public void Empty_nan_or_broken_answers_give_nothing(string json) => Scalar(json).Should().BeNull();

    [Fact]
    public void Health_pairs_are_read_and_bad_ones_skipped()
    {
        var targets = SystemStatsReader.ParseHealth(
            ["api=http://api-1:7080/health/ready", " api = http://api-2:7080/health/ready ", "web", "=http://x", "bad=not a url", "workers=http://workers-1:7101/health/ready"]);

        targets.Select(t => (t.Service, t.Url)).Should().Equal(
            ("api", "http://api-1:7080/health/ready"), ("api", "http://api-2:7080/health/ready"), ("workers", "http://workers-1:7101/health/ready"));
    }

    [Fact]
    public void Every_tile_has_a_query_and_the_names_match_the_exported_metrics()
    {
        SystemStatsReader.Queries.Keys.Should().BeEquivalentTo("bids", "errors", "p99", "lag", "online");
        SystemStatsReader.Queries["bids"].Should().Contain("bids_total");
        SystemStatsReader.Queries["p99"].Should().Contain("bid_latency_ms").And.Contain("histogram_quantile(0.99");
        SystemStatsReader.Queries["lag"].Should().Contain("persist_lag_ms");
        SystemStatsReader.Queries["online"].Should().Contain("circuits_active");
    }

    [Fact]
    public void Without_a_prometheus_address_the_defaults_leave_those_tiles_empty()
    {
        var options = new SystemStatsOptions();

        options.PrometheusUrl.Should().BeEmpty();
        options.Health.Should().BeEmpty();
        SystemStatsReader.CacheFor.Should().Be(TimeSpan.FromSeconds(5));
    }
}
