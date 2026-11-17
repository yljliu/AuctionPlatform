using Auction.Contracts.Hosting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;

// Simulator host. Bots, scenarios and the verifier come in spec 08.
// For now it only answers health and metrics on port 7201, so the full stack can start.

if (await HealthProbe.TryRunAsync(args) is int probeResult)
{
    return probeResult;
}

var builder = WebApplication.CreateBuilder(args);
var instance = builder.Configuration["INSTANCE_NAME"] ?? Environment.MachineName;

builder.Host.UseSerilog((_, log) =>
{
    log.MinimumLevel.Information()
        .Enrich.WithProperty("Service", "simulator")
        .Enrich.WithProperty("Instance", instance)
        .WriteTo.Console();

    var seqUrl = builder.Configuration["Seq:Url"];
    if (!string.IsNullOrWhiteSpace(seqUrl))
    {
        log.WriteTo.Seq(seqUrl);
    }
});

builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(builder.Configuration.GetValue("Ports:Http", 7201)));
builder.Services.AddHealthChecks();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("auction-simulator", serviceInstanceId: instance))
    .WithMetrics(m => m.AddPrometheusExporter());

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();
return 0;
