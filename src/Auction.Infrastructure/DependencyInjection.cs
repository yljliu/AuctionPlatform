using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Auction.Infrastructure;

public static class DependencyInjection
{
    /// <summary>SQL, Redis and the shared services used by Api and Workers.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string sqlConnectionString, string redisConnectionString,
        string? imagesPath = null, RedisOptions? redisOptions = null)
    {
        services.AddSingleton(new ImageStoreOptions { RootPath = string.IsNullOrWhiteSpace(imagesPath) ? "/data/images" : imagesPath });
        services.AddSingleton((redisOptions ?? new RedisOptions()).Validate());

        services.AddDbContext<AuctionDbContext>(o =>
            o.UseSqlServer(sqlConnectionString, sql => sql.EnableRetryOnFailure(3)));

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisConnectionString);
            options.AbortOnConnectFail = false;   // keep retrying in the background if Redis is not up yet
            return ConnectionMultiplexer.Connect(options);
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IRateLimiter, RedisRateLimiter>();
        services.AddScoped<UserStatusCache>();
        services.AddScoped<AuthManager>();
        services.AddScoped<UserAdminManager>();
        services.AddScoped<CategoryRepository>();
        services.AddScoped<SellerGuard>();
        services.AddScoped<ItemManager>();
        services.AddScoped<ImageManager>();
        services.AddScoped<OutboxWriter>();
        services.AddScoped<AuctionManager>();
        services.AddScoped<AuctionQueries>();
        services.AddSingleton<LiveStateReader>();
        services.AddScoped<AuctionBrowser>();
        services.AddScoped<AuctionDetailReader>();
        services.AddScoped<Bidding.IncrementTableRepository>();
        services.AddSingleton<ScriptRegistry>();
        services.AddSingleton<Bidding.BidScript>();
        services.AddScoped<Bidding.BidService>();
        services.AddScoped<Bidding.BidReadService>();
        services.AddSingleton(new Bidding.Persistence.SqlConnectionFactory(sqlConnectionString));
        services.AddSingleton<Bidding.Persistence.PersistSqlWriter>();
        services.AddSingleton<Live.AuctionEventBus>();
        services.AddSingleton<Live.UserHandleCache>();
        services.AddScoped<Live.SnapshotBuilder>();
        services.AddSingleton(new Live.LiveOptions());
        services.AddScoped<Live.AuctionStreamer>();
        services.AddSingleton<ILiveAuctionControl, StubLiveAuctionControl>();   // real one in spec 05

        return services;
    }
}
