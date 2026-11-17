using Auction.Domain.Enums;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Seed;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Auction.Integration.Tests;

[Collection(SqlServerCollection.Name)]
public class SeedRunnerTests(SqlServerFixture sql)
{
    private const string Password = "Test_seed_password_123";

    private static async Task<int[]> CountRowsAsync(AuctionDbContext db) =>
    [
        await db.Users.CountAsync(),
        await db.UserRoles.CountAsync(),
        await db.SellerProfiles.CountAsync(),
        await db.Categories.CountAsync(),
        await db.BidIncrementTiers.CountAsync(),
        await db.FeeSchedules.CountAsync(),
        await db.Items.CountAsync(),
        await db.Auctions.CountAsync(),
    ];

    [Fact]
    public async Task Seed_creates_the_demo_data()
    {
        await using var db = await sql.CreateEmptyContextAsync("seed_creates");
        await db.Database.MigrateAsync();

        await new SeedRunner(db, Password, DateTime.UtcNow).RunAsync();

        (await db.Users.CountAsync()).Should().Be(9);
        (await db.SellerProfiles.CountAsync(p => p.Status == SellerStatus.Verified)).Should().Be(3);
        (await db.UserRoles.CountAsync(r => r.Role == Role.Admin)).Should().Be(1);
        (await db.Categories.CountAsync(c => c.ParentCategoryId == null)).Should().Be(3);
        (await db.Categories.CountAsync()).Should().Be(9);
        (await db.BidIncrementTiers.CountAsync(t => t.TableId == 1)).Should().Be(5);
        (await db.FeeSchedules.CountAsync()).Should().Be(1);
        (await db.Auctions.CountAsync(a => a.Status == AuctionStatus.Live)).Should().Be(4);
        (await db.Auctions.CountAsync(a => a.Status == AuctionStatus.Scheduled)).Should().Be(2);
    }

    [Fact]
    public async Task Seed_twice_does_not_add_rows()
    {
        await using var db = await sql.CreateEmptyContextAsync("seed_twice");
        await db.Database.MigrateAsync();

        await new SeedRunner(db, Password, DateTime.UtcNow).RunAsync();
        var first = await CountRowsAsync(db);

        await new SeedRunner(db, Password, DateTime.UtcNow.AddMinutes(1)).RunAsync();
        var second = await CountRowsAsync(db);

        second.Should().Equal(first);
    }

    [Fact]
    public async Task Seeded_password_matches_the_bcrypt_hash()
    {
        await using var db = await sql.CreateEmptyContextAsync("seed_password");
        await db.Database.MigrateAsync();

        await new SeedRunner(db, Password, DateTime.UtcNow).RunAsync();

        var alice = await db.Users.SingleAsync(u => u.Username == "alice");
        BCrypt.Net.BCrypt.Verify(Password, alice.PasswordHash).Should().BeTrue();
        alice.PasswordHash.Should().NotContain(Password);
    }

    [Fact]
    public void Short_password_is_refused()
    {
        var act = () => new SeedRunner(null!, "short", DateTime.UtcNow);
        act.Should().Throw<ArgumentException>();
    }
}
