using Auction.Api.Interceptors;
using Auction.Api.Security;
using Auction.Api.Services;
using Auction.Contracts.Hosting;
using Auction.Infrastructure;
using Auction.Infrastructure.Identity;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

// Container health check: dotnet Auction.Api.dll --probe http://localhost:7080/health/ready
if (await HealthProbe.TryRunAsync(args) is int probeResult)
{
    return probeResult;
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var instance = config["INSTANCE_NAME"] ?? Environment.MachineName;

// ---------- logging ----------
builder.Host.UseSerilog((_, log) =>
{
    log.MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "api")
        .Enrich.WithProperty("Instance", instance)
        .WriteTo.Console();

    var seqUrl = config["Seq:Url"];
    if (!string.IsNullOrWhiteSpace(seqUrl))
    {
        log.WriteTo.Seq(seqUrl);
    }
});

// ---------- ports: gRPC needs HTTP/2; health and metrics use HTTP/1 ----------
var grpcPort = config.GetValue("Ports:Grpc", 7001);
var httpPort = config.GetValue("Ports:Http", 7080);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(grpcPort, listen => listen.Protocols = HttpProtocols.Http2);
    kestrel.ListenAnyIP(httpPort, listen => listen.Protocols = HttpProtocols.Http1);
});

// ---------- infrastructure ----------
var sql = config.GetConnectionString("Sql") ?? throw new InvalidOperationException("ConnectionStrings__Sql is not set.");
var redis = config.GetConnectionString("Redis") ?? throw new InvalidOperationException("ConnectionStrings__Redis is not set.");
builder.Services.AddInfrastructure(sql, redis, config["Images:Path"],
    config.GetSection("Redis").Get<Auction.Infrastructure.Redis.RedisOptions>());

var jwt = config.GetSection("JWT").Get<JwtOptions>() ?? new JwtOptions();
var tokenService = new TokenService(jwt, TimeProvider.System);   // throws if the key is too short
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton(tokenService);

// ---------- auth: JwtBearer reads the token; AuthorizationInterceptor decides ----------
builder.Services.AddAuthentication().AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = tokenService.SigningKey,
        NameClaimType = AuthClaims.Name,
        RoleClaimType = AuthClaims.Role,
        ClockSkew = TimeSpan.FromSeconds(30),
    };
});
builder.Services.AddAuthorization();

// ---------- gRPC: interceptors run in this order ----------
builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<ExceptionInterceptor>();
    options.Interceptors.Add<AuthorizationInterceptor>();
    options.Interceptors.Add<RateLimitInterceptor>();
});

// ---------- live updates: read the partition streams into the in-process bus (spec 04) ----------
builder.Services.AddHostedService<Auction.Api.Live.LiveStreamWorker>();
builder.Services.AddSingleton<Auction.Api.Live.UserEventHub>();   // bell feed fan-out (spec 07)

// ---------- health and metrics ----------
builder.Services.AddHealthChecks()
    .AddSqlServer(sql, name: "sql", tags: ["ready"])
    .AddRedis(redis, name: "redis", tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("auction-api", serviceInstanceId: instance))
    .WithTracing(t => t.AddAspNetCoreInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation()
        .AddMeter(Auction.Infrastructure.Bidding.BiddingMetrics.MeterName)
        .AddMeter(Auction.Infrastructure.Live.LiveMetrics.MeterName)
        .AddPrometheusExporter());

var app = builder.Build();

app.UseMiddleware<CorrelationMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapGrpcService<AuthGrpcService>();
app.MapGrpcService<AdminGrpcService>();
app.MapGrpcService<CatalogGrpcService>();
app.MapGrpcService<SellerGrpcService>();
app.MapGrpcService<BiddingGrpcService>();
app.MapGrpcService<LiveGrpcService>();
app.MapGrpcService<WatchGrpcService>();          // spec 07
app.MapGrpcService<NotificationGrpcService>();   // spec 07

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();
return 0;
