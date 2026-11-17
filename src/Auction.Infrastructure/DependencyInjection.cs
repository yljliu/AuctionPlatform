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
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string sqlConnectionString, string redisConnectionString)
    {
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

        return services;
    }
}
