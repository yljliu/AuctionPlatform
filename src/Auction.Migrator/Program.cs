using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;

// Applies pending EF Core migrations, then seeds demo data when started with --seed, then exits.
// Exit code 0 = ok, 1 = failed.

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Sql");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings__Sql is not set.");
    return 1;
}

var seed = args.Contains("--seed");

var options = new DbContextOptionsBuilder<AuctionDbContext>()
    .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(5))
    .Options;

try
{
    await using var db = new AuctionDbContext(options);

    Console.WriteLine("Applying migrations...");
    await db.Database.MigrateAsync();
    Console.WriteLine("Migrations applied.");

    if (seed)
    {
        var password = Environment.GetEnvironmentVariable("SEED_PASSWORD") ?? "";
        Console.WriteLine("Seeding demo data...");
        await new SeedRunner(db, password, DateTime.UtcNow).RunAsync();
        Console.WriteLine("Seed done.");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Migration failed: {ex}");
    return 1;
}
