using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Auction.Integration.Tests.Fixtures;

/// <summary>
/// One Redis for the test run. Uses TEST_REDIS (for example "localhost:6379") if set,
/// otherwise starts a Testcontainers Redis.
/// </summary>
public class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_REDIS");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _container = new RedisBuilder().WithImage("redis:7.2").Build();
            await _container.StartAsync();
            connectionString = _container.GetConnectionString();
        }

        // Admin mode lets tests run SCRIPT FLUSH (the "Redis forgot the script" test).
        var options = ConfigurationOptions.Parse(connectionString);
        options.AllowAdmin = true;
        Connection = await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async Task DisposeAsync()
    {
        Connection?.Dispose();
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}
