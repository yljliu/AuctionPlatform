using Auction.Contracts.Hosting;
using Auction.Infrastructure.Identity;
using Auction.Simulator.Sim;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.IdentityModel.Tokens;

// Simulator host (spec 08): SimulatorService on gRPC port 7201 (admin token only), health and metrics on 7202.
// Also runs the one-off checks from earlier specs:
//   dotnet run --project src/Auction.Simulator -- load-check [bidsPerSecond] [seconds] [auctions]   (spec 03 task 9)
//   dotnet run --project src/Auction.Simulator -- failover-check [stop|kill]                         (spec 05 task 7)
//   dotnet run --project src/Auction.Simulator -- sim-check [bots] [auctions] [minutes] [rate]       (spec 08 task 8)

if (await HealthProbe.TryRunAsync(args) is int probeResult)
{
    return probeResult;
}

if (args.Length > 0 && args[0] == "load-check")
{
    return await Auction.Simulator.LoadCheck.RunAsync(args);
}

if (args.Length > 0 && args[0] == "failover-check")
{
    return await Auction.Simulator.FailoverCheck.RunAsync(args);
}

// Spec 08 task 8: dotnet run --project src/Auction.Simulator -- sim-check [bots] [auctions] [minutes] [rate]
if (args.Length > 0 && args[0] == "sim-check")
{
    return await Auction.Simulator.SimCheck.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var instance = config["INSTANCE_NAME"] ?? Environment.MachineName;

builder.AddAuctionObservability(new ObservabilityOptions("simulator", instance, Tracing: false));
// gRPC needs HTTP/2; the health probe and Prometheus use HTTP/1.
var grpcPort = config.GetValue("Ports:Grpc", 7201);
var httpPort = config.GetValue("Ports:Http", 7202);
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(grpcPort, l => l.Protocols = HttpProtocols.Http2);
    k.ListenAnyIP(httpPort, l => l.Protocols = HttpProtocols.Http1);
});

// The admin's token, checked with the Api's key (design 1).
var jwt = config.GetSection("JWT").Get<JwtOptions>() ?? new JwtOptions();
var tokens = new TokenService(jwt, TimeProvider.System);
builder.Services.AddAuthentication().AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer, ValidAudience = jwt.Audience, IssuerSigningKey = tokens.SigningKey,
        NameClaimType = AuthClaims.Name, RoleClaimType = AuthClaims.Role, ClockSkew = TimeSpan.FromSeconds(30),
    };
});
builder.Services.AddAuthorization();
builder.Services.AddGrpc(o => o.Interceptors.Add<AdminOnlyInterceptor>());

var sql = config.GetConnectionString("Sql") ?? throw new InvalidOperationException("ConnectionStrings__Sql is not set.");
var apis = config.GetSection("Api:Endpoints").Get<string[]>() ?? ["http://localhost:7001"];
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IApiPort>(_ => new GrpcApiPort(apis));
builder.Services.AddSingleton<IRunStore>(sp => new SqlRunStore(sql, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<RunManager>();

builder.Services.AddHealthChecks();


var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapGrpcService<SimulatorGrpcService>().RequireHost($"*:{grpcPort}");
app.MapAuctionObservability($"*:{httpPort}");

app.Logger.LogInformation("Simulator {Instance} ready: gRPC on {Grpc}, health on {Http}, Api {Apis}", instance, grpcPort, httpPort, string.Join(",", apis));
app.Run();
return 0;
