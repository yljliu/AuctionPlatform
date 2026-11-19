using Auction.Api.Interceptors;
using Auction.Api.Security;
using Auction.Api.Services;
using Auction.Contracts.Hosting;
using Auction.Infrastructure;
using Auction.Infrastructure.Observability;
using Auction.Infrastructure.Identity;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.IdentityModel.Tokens;

// Container health check: dotnet Auction.Api.dll --probe http://localhost:7080/health/ready
if (await HealthProbe.TryRunAsync(args) is int probeResult)
{
    return probeResult;
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var instance = config["INSTANCE_NAME"] ?? Environment.MachineName;

// ---------- logging ----------
builder.AddAuctionObservability(new ObservabilityOptions("api", instance, GrpcServerMetrics: true));
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

// System stats for the admin page (spec 08 task 7).
builder.Services.AddHttpClient("stats", c => c.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddSingleton(config.GetSection("SystemStats").Get<Auction.Api.Admin.SystemStatsOptions>() ?? new Auction.Api.Admin.SystemStatsOptions());
builder.Services.AddSingleton<Auction.Api.Admin.SystemStatsReader>();

// ---------- health and metrics ----------
builder.Services.AddHealthChecks()
    .AddSqlServer(sql, name: "sql", tags: ["ready"])
    .AddRedis(redis, name: "redis", tags: ["ready"])
    .AddCheck<RedisReplicaHealthCheck>("redis-replica", tags: ["ready"]);

// Redis replica state for redis_replica_connected and the ready check (spec 09).
builder.Services.AddHostedService<RedisInfoMonitor>();

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

app.MapAuctionObservability();

app.Run();
return 0;
