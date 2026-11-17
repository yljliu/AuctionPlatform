using Auction.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace Auction.Integration.Tests.Fixtures;

/// <summary>
/// Gives each test its own empty database on one SQL Server.
/// If TEST_SQL is set (for example the compose lite sqlserver), that server is used.
/// Otherwise a Testcontainers SQL Server is started for the test run.
/// </summary>
public class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;
    private string _serverConnectionString = "";

    public async Task InitializeAsync()
    {
        var fromEnv = Environment.GetEnvironmentVariable("TEST_SQL");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            _serverConnectionString = fromEnv;
        }
        else
        {
            _container = new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            _serverConnectionString = _container.GetConnectionString();
        }

        await WaitUntilAcceptingConnectionsAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    public string ConnectionStringFor(string database)
    {
        var builder = new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = database,
            TrustServerCertificate = true,
            ConnectTimeout = 30,
        };
        return builder.ConnectionString;
    }

    /// <summary>Drops the database if it exists, so every run starts empty.</summary>
    public async Task<AuctionDbContext> CreateEmptyContextAsync(string database)
    {
        var db = CreateContext(database);
        await db.Database.EnsureDeletedAsync();
        return db;
    }

    public AuctionDbContext CreateContext(string database)
    {
        var options = new DbContextOptionsBuilder<AuctionDbContext>()
            .UseSqlServer(ConnectionStringFor(database))
            .Options;
        return new AuctionDbContext(options);
    }

    // SQL Server can report "started" before it answers clients on a busy machine.
    // Pooling is off here so one failed try does not block the next.
    private async Task WaitUntilAcceptingConnectionsAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionStringFor("master")) { Pooling = false };
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            try
            {
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (SqlException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2000);
            }
        }
    }
}

[CollectionDefinition(Name)]
public class SqlServerCollection : ICollectionFixture<SqlServerFixture>, ICollectionFixture<RedisFixture>
{
    public const string Name = "sql";
}