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
builder.Services.AddMudServices(mud =>
{
    mud.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight;   // design 1
    mud.SnackbarConfiguration.MaxDisplayedSnackbars = 3;
});

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
builder.Services.AddGrpcClient<BiddingService.BiddingServiceClient>(UseApi)
    .ConfigureChannel(ApiChannel)
    .AddInterceptor<AuthClientInterceptor>(InterceptorScope.Client);
builder.Services.AddGrpcClient<SellerService.SellerServiceClient>(UseApi)
    .ConfigureChannel(ApiChannel)
    .AddInterceptor<AuthClientInterceptor>(InterceptorScope.Client);
builder.Services.AddGrpcClient<AdminService.AdminServiceClient>(UseApi)
    .ConfigureChannel(ApiChannel)
    .AddInterceptor<AuthClientInterceptor>(InterceptorScope.Client);

builder.Services.AddGrpcClient<WatchService.WatchServiceClient>(UseApi)
    .ConfigureChannel(ApiChannel)
    .AddInterceptor<AuthClientInterceptor>(InterceptorScope.Client);
builder.Services.AddGrpcClient<NotificationService.NotificationServiceClient>(UseApi)
    .ConfigureChannel(ApiChannel)
    .AddInterceptor<AuthClientInterceptor>(InterceptorScope.Client);

// Simulator (spec 08): its own address; the admin's token goes with every call.
var simulatorUrl = config["Simulator:Url"];
builder.Services.AddGrpcClient<SimulatorService.SimulatorServiceClient>(o => o.Address = new Uri(string.IsNullOrWhiteSpace(simulatorUrl) ? "http://localhost:7201" : simulatorUrl))
    .ConfigureChannel(o => o.Credentials = ChannelCredentials.Insecure)
    .AddInterceptor<AuthClientInterceptor>(InterceptorScope.Client);
builder.Services.AddSingleton(new Auction.Web.Services.Admin.SimulatorSettings(!string.IsNullOrWhiteSpace(simulatorUrl)));

// Online users for the admin tiles (spec 08).
builder.Services.AddSingleton<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, Auction.Web.Services.Admin.CircuitCounter>();

// Bell feed (spec 07): one per browser connection.
builder.Services.AddScoped<Auction.Web.Services.Notify.IUserFeedSource, Auction.Web.Services.Notify.GrpcUserFeedSource>();
builder.Services.AddScoped<Auction.Web.Services.Notify.UserFeed>();

// ---------- pages (spec 06) ----------
builder.Services.AddScoped<Auction.Web.Services.Catalog.CategoryCache>();   // the tree itself is in IMemoryCache
builder.Services.AddScoped<Auction.Web.Services.Bidding.IBidClient, Auction.Web.Services.Bidding.GrpcBidClient>();
builder.Services.AddScoped<Auction.Web.Services.Bidding.BidSender>();
builder.Services.AddScoped<Auction.Web.Services.Seller.ImageUploader>();
builder.Services.AddSingleton(config.GetSection("Demo").Get<Auction.Web.Services.Ui.DemoOptions>() ?? new Auction.Web.Services.Ui.DemoOptions());

// Live updates (spec 04): public, so this client sends no user token. One shared hub per web copy.
builder.Services.AddGrpcClient<LiveService.LiveServiceClient>(UseApi).ConfigureChannel(ApiChannel);
builder.Services.AddSingleton<Auction.Web.Services.Live.ILiveFeedSource, Auction.Web.Services.Live.GrpcLiveFeedSource>();
builder.Services.AddSingleton(new Auction.Web.Services.Live.LiveHubOptions());
builder.Services.AddSingleton<Auction.Web.Services.Live.LiveAuctionHub>();
builder.Services.AddScoped<Auction.Web.Services.Live.ServerClock>();   // clock offset, one per browser connection

// Image bytes cache: at most 100 MB in memory per web copy.
builder.Services.AddMemoryCache(options => options.SizeLimit = 100 * 1024 * 1024);

// ---------- health and metrics ----------
builder.Services.AddHealthChecks().AddRedis(redis, name: "redis", tags: ["ready"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("auction-web", serviceInstanceId: instance))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddGrpcClientInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation()
        .AddMeter(Auction.Web.Services.Live.WebLiveMetrics.MeterName, Auction.Web.Services.Admin.CircuitCounter.MeterName)
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
