using Auction.Contracts.Hosting;
using Auction.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Events;

// Background workers host. Workers (persister, lifecycle, outbox relay, notifier) are added in specs 03, 05 and 07.
// Port 7101 serves only health and metrics.

if (await HealthProbe.TryRunAsync(args) is int probeResult)
{
    return probeResult;
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var instance = config["INSTANCE_NAME"] ?? Environment.MachineName;

builder.Host.UseSerilog((_, log) =>
{
    log.MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "workers")
        .Enrich.WithProperty("Instance", instance)
        .WriteTo.Console();

    var seqUrl = config["Seq:Url"];
    if (!string.IsNullOrWhiteSpace(seqUrl))
    {
        log.WriteTo.Seq(seqUrl);
    }
});

builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(config.GetValue("Ports:Http", 7101)));

var sql = config.GetConnectionString("Sql") ?? throw new InvalidOperationException("ConnectionStrings__Sql is not set.");
var redis = config.GetConnectionString("Redis") ?? throw new InvalidOperationException("ConnectionStrings__Redis is not set.");
builder.Services.AddInfrastructure(sql, redis);

builder.Services.AddHealthChecks()
    .AddSqlServer(sql, name: "sql", tags: ["ready"])
    .AddRedis(redis, name: "redis", tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("auction-workers", serviceInstanceId: instance))
    .WithMetrics(m => m.AddPrometheusExporter());

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapPrometheusScrapingEndpoint("/metrics");

app.Logger.LogInformation("Workers host {Instance} started; no workers registered yet", instance);
app.Run();
return 0;
