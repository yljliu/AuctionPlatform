using Auction.Contracts.Hosting;
using Auction.Infrastructure;
using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Lifecycle;
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
builder.Services.AddInfrastructure(sql, redis, redisOptions: config.GetSection("Redis").Get<Auction.Infrastructure.Redis.RedisOptions>());

builder.Services.AddHealthChecks()
    .AddSqlServer(sql, name: "sql", tags: ["ready"])
    .AddRedis(redis, name: "redis", tags: ["ready"]);

// Persister: moves bids from the Redis stream to SQL (spec 03 task 8).
var persisterOptions = config.GetSection("Persister").Get<PersisterOptions>() ?? new PersisterOptions();
persisterOptions.Consumer = instance;
builder.Services.AddSingleton(persisterOptions);
builder.Services.AddHostedService<Auction.Workers.PersisterWorker>();

// Lifecycle lease: only one workers copy runs lifecycle work (spec 05 task 1).
// The holder name is unique per process, so a restarted copy counts as a new holder (token + 1).
builder.Services.AddSingleton(new LeaseOptions { Holder = $"{instance}:{Guid.NewGuid():N}" });
builder.Services.AddSingleton<ILeaseStore, SqlLeaseStore>();
builder.Services.AddSingleton<LeaseManager>();
builder.Services.AddHostedService<Auction.Workers.LeaseWorker>();

// Lifecycle loop: load, open, closing-soon, seal and settle, only on the lease holder (spec 05 tasks 3, 4 and 6).
builder.Services.AddSingleton(config.GetSection("Lifecycle").Get<LifecycleOptions>() ?? new LifecycleOptions());
builder.Services.AddSingleton<LifecycleSql>();
builder.Services.AddSingleton<Settler>();
builder.Services.AddSingleton<LifecycleLoop>();
builder.Services.AddHostedService<Auction.Workers.LifecycleWorker>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("auction-workers", serviceInstanceId: instance))
    .WithMetrics(m => m.AddMeter(PersisterMetrics.MeterName, LifecycleMetrics.MeterName).AddPrometheusExporter());

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapPrometheusScrapingEndpoint("/metrics");

app.Logger.LogInformation("Workers host {Instance} started with the persister", instance);
app.Run();
return 0;
