using Auction.Infrastructure.Live;
using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Auction.Integration.Tests.Fixtures;

/// <summary>Builds a UserHandleCache whose SQL lookups go to the given test database.</summary>
public static class HandleCacheFactory
{
    public static UserHandleCache Create(IConnectionMultiplexer redis, string sqlConnectionString)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AuctionDbContext>(o => o.UseSqlServer(sqlConnectionString));
        var provider = services.BuildServiceProvider();

        return new UserHandleCache(redis, provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<UserHandleCache>.Instance);
    }
}
