using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Auction.Contracts.Hosting;

/// <summary>What one service tells AddAuctionObservability about itself.</summary>
public sealed record ObservabilityOptions(
    string Service,
    string Instance,
    bool Tracing = true,
    bool GrpcClientTracing = false,
    bool GrpcServerMetrics = false,
    string? RequireHost = null);

/// <summary>One call per service sets up logs, metrics and traces the same way (spec 09 R1).</summary>
public static class ObservabilityExtensions
{
    /// <summary>The meters every service listens to. Instruments named "Auction.*" are all exported.</summary>
    public const string MeterPattern = "Auction.*";

    public static WebApplicationBuilder AddAuctionObservability(this WebApplicationBuilder builder, ObservabilityOptions options)
    {
        var config = builder.Configuration;
        var seqUrl = config["Seq:Url"];

        // Logs: JSON on the console (for docker logs) and to Seq.
        builder.Host.UseSerilog((_, log) =>
        {
            log.MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service", options.Service)
                .Enrich.WithProperty("instance", options.Instance)
                .Destructure.With<SensitiveDataDestructuringPolicy>()
                .WriteTo.Console(new CompactJsonFormatter());

            if (!string.IsNullOrWhiteSpace(seqUrl))
            {
                log.WriteTo.Seq(seqUrl);
            }
        });

        // Metrics and traces.
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService($"auction-{options.Service}", serviceInstanceId: options.Instance));

        otel.WithMetrics(m =>
        {
            m.AddRuntimeInstrumentation().AddMeter(MeterPattern);
            if (options.Tracing)
            {
                m.AddAspNetCoreInstrumentation();
            }

            if (options.GrpcServerMetrics)
            {
                m.AddMeter("Grpc.AspNetCore.Server");
            }

            m.AddPrometheusExporter();
        });

        if (options.Tracing)
        {
            otel.WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation().AddSource(MeterPattern);
                if (options.GrpcClientTracing)
                {
                    t.AddGrpcClientInstrumentation();
                }

                if (!string.IsNullOrWhiteSpace(seqUrl))
                {
                    t.AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(seqUrl.TrimEnd('/') + "/ingest/otlp/v1/traces");
                        o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    });
                }
            });
        }

        return builder;
    }

    /// <summary>Maps /health/live, /health/ready and /metrics. Pass the host pattern when the service has more than one port.</summary>
    public static WebApplication MapAuctionObservability(this WebApplication app, string? requireHost = null)
    {
        var live = app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        var ready = app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
        var metrics = app.MapPrometheusScrapingEndpoint("/metrics");
        if (requireHost != null)
        {
            live.RequireHost(requireHost);
            ready.RequireHost(requireHost);
            metrics.RequireHost(requireHost);
        }

        return app;
    }
}