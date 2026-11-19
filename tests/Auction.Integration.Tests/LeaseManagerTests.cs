using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Persistence;
using Auction.Integration.Tests.Fixtures;
using Dapper;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>Spec 05 task 1 on a real SQL Server: the lease statement, takeover and the fencing check (R1.1–R1.3).</summary>
[Collection(SqlServerCollection.Name)]
public class LeaseManagerTests(SqlServerFixture sql) : IAsyncLifetime
{
    private AuctionDbContext _db = null!;
    private SqlConnectionFactory _connections = null!;

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("lease");
        await _db.Database.MigrateAsync();   // the migration seeds the 'lifecycle' row
        _connections = new SqlConnectionFactory(sql.ConnectionStringFor("lease"));
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private LeaseManager Manager(string holder, TimeSpan? ttl = null) => new(
        new SqlLeaseStore(_connections),
        new LeaseOptions { Holder = holder, Ttl = ttl ?? TimeSpan.FromSeconds(15), SafetyMargin = TimeSpan.Zero },
        TimeProvider.System,
        NullLogger<LeaseManager>.Instance);

    [Fact]
    public async Task Migration_seeds_one_free_lease_row()
    {
        await using var connection = _connections.Create();
        var rows = (await connection.QueryAsync<(string Holder, long Token)>(
            "SELECT Holder, FencingToken FROM bidding.LifecycleLeases WHERE LeaseName = 'lifecycle'")).ToList();

        rows.Should().Equal(("", 0L));
    }

    [Fact]
    public async Task Two_managers_give_one_leader()
    {
        var a = Manager("a");
        var b = Manager("b");

        var results = await Task.WhenAll(a.RenewOnceAsync(), b.RenewOnceAsync());

        results.Count(r => r).Should().Be(1, "exactly one copy wins the lease");
        new[] { a.FencingToken, b.FencingToken }.Should().BeEquivalentTo(new[] { 0L, 1L });
    }

    [Fact]
    public async Task When_the_leader_stops_renewing_the_other_takes_over_with_token_plus_one()
    {
        var a = Manager("a", ttl: TimeSpan.FromSeconds(1));
        var b = Manager("b", ttl: TimeSpan.FromSeconds(1));
        (await a.RenewOnceAsync()).Should().BeTrue();
        var first = a.FencingToken;

        (await b.RenewOnceAsync()).Should().BeFalse("a still holds it");
        await Task.Delay(1500);   // a never renews

        (await b.RenewOnceAsync()).Should().BeTrue();
        b.FencingToken.Should().Be(first + 1);
        (await a.RenewOnceAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Renew_keeps_the_token()
    {
        var a = Manager("a");
        await a.RenewOnceAsync();
        var first = a.FencingToken;

        (await a.RenewOnceAsync()).Should().BeTrue();

        a.FencingToken.Should().Be(first);
    }

    [Fact]
    public async Task Release_lets_the_other_take_over_at_once()
    {
        var a = Manager("a");
        var b = Manager("b");
        await a.RenewOnceAsync();

        await a.ReleaseAsync();

        (await b.RenewOnceAsync()).Should().BeTrue();
        b.FencingToken.Should().Be(2);
    }

    [Fact]
    public async Task Current_token_passes_the_fencing_check()
    {
        var a = Manager("a");
        await a.RenewOnceAsync();

        await using var connection = _connections.Create();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        var check = () => Fencing.EnsureAsync(connection, transaction, a.Current!);

        await check.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Stale_token_write_throws()
    {
        var a = Manager("a", ttl: TimeSpan.FromSeconds(1));
        var b = Manager("b", ttl: TimeSpan.FromSeconds(1));
        await a.RenewOnceAsync();
        var oldGrant = a.Current!;
        await Task.Delay(1500);
        (await b.RenewOnceAsync()).Should().BeTrue();

        await using var connection = _connections.Create();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        var write = () => Fencing.EnsureAsync(connection, transaction, oldGrant);

        (await write.Should().ThrowAsync<StaleFencingTokenException>()).Which.Grant.Token.Should().Be(oldGrant.Token);
    }

    [Fact]
    public async Task Expired_lease_fails_the_fencing_check_even_for_the_same_holder()
    {
        var a = Manager("a", ttl: TimeSpan.FromSeconds(1));
        await a.RenewOnceAsync();
        var grant = a.Current!;
        await Task.Delay(1500);

        await using var connection = _connections.Create();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        var write = () => Fencing.EnsureAsync(connection, transaction, grant);

        await write.Should().ThrowAsync<StaleFencingTokenException>();
    }
}
