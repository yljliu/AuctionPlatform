using Auction.Contracts;
using Auction.Contracts.Hosting;
using Auction.Web.Auth;
using Auction.Web.Images;
using Auction.Web.Components;
using Grpc.Core;
using Grpc.Net.Client.Balancer;
using Grpc.Net.Client.Configuration;
using Grpc.Net.ClientFactory;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MudBlazor.Services;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

// Container health check: dotnet Auction.Web.dll --probe http://localhost:8080/health/ready
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
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "web")
        .Enrich.WithProperty("Instance", instance)
        .WriteTo.Console();

    var seqUrl = config["Seq:Url"];
    if (!string.IsNullOrWhiteSpace(seqUrl))
    {
        log.WriteTo.Seq(seqUrl);
    }
});

var redis = config.GetConnectionString("Redis") ?? throw new InvalidOperationException("ConnectionStrings__Redis is not set.");
var apiEndpoints = config.GetSection("Api:Endpoints").Get<string[]>() ?? ["http://localhost:7001"];

// ---------- Blazor and MudBlazor ----------
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();

// ---------- sign-in: encrypted cookie with the user and a session id ----------
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".Auction.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/account/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = false;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// All web copies must share Data Protection keys, or one copy cannot read another's cookie.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("auction-web");
var keysPath = config["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redis;
    options.InstanceName = "web:";
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITokenSessionStore, TokenSessionStore>();
builder.Services.AddScoped<TokenStore>();
builder.Services.AddScoped<AuthClientInterceptor>();

// ---------- gRPC clients: one channel, round robin over all Api copies ----------
builder.Services.AddSingleton<ResolverFactory>(new StaticResolverFactory(_ =>
    apiEndpoints.Select(e => new Uri(e)).Select(u => new BalancerAddress(u.Host, u.Port)).ToArray()));

void UseApi(GrpcClientFactoryOptions options) => options.Address = new Uri("static:///api");

void ApiChannel(Grpc.Net.Client.GrpcChannelOptions options)
{
    options.Credentials = ChannelCredentials.Insecure;
    options.ServiceConfig = new ServiceConfig { LoadBalancingConfigs = { new RoundRobinConfig() } };
}

builder.Services.AddGrpcClient<AuthService.AuthServiceClient>(UseApi)
    .ConfigureChannel(ApiChannel)
    .AddInterceptor<AuthClientInterceptor>(InterceptorScope.Client);
builder.Services.AddGrpcClient<CatalogService.CatalogServiceClient>(UseApi)
    .ConfigureChannel(ApiChannel)
    .AddInterceptor<AuthClientInterceptor>(InterceptorScope.Client);

// Live updates (spec 04): public, so this client sends no user token. One shared hub per web copy.
builder.Services.AddGrpcClient<LiveService.LiveServiceClient>(UseApi).ConfigureChannel(ApiChannel);
builder.Services.AddSingleton<Auction.Web.Services.Live.ILiveFeedSource, Auction.Web.Services.Live.GrpcLiveFeedSource>();
builder.Services.AddSingleton(new Auction.Web.Services.Live.LiveHubOptions());
builder.Services.AddSingleton<Auction.Web.Services.Live.LiveAuctionHub>();

// Image bytes cache: at most 100 MB in memory per web copy.
builder.Services.AddMemoryCache(options => options.SizeLimit = 100 * 1024 * 1024);

// ---------- health and metrics ----------
builder.Services.AddHealthChecks().AddRedis(redis, name: "redis", tags: ["ready"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("auction-web", serviceInstanceId: instance))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddGrpcClientInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation()
        .AddMeter(Auction.Web.Services.Live.WebLiveMetrics.MeterName)
        .AddPrometheusExporter());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapAccountEndpoints();
app.MapImageEndpoints();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapPrometheusScrapingEndpoint("/metrics");

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
return 0;
