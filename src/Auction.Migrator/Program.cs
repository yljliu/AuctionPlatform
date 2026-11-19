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

        // Demo catalog with drawn photos. The images folder must be the Api's (compose mounts the same volume).
        var imagesRoot = Environment.GetEnvironmentVariable("Images__Path") ?? "/data/images";
        var catalog = new DemoCatalog(db, imagesRoot, DateTime.UtcNow);
        var added = await catalog.RunAsync();
        var pictured = await catalog.AddMissingPicturesAsync(SeedRunner.DemoItemPictures());
        Console.WriteLine($"Seed done. Catalog: {added} new auctions, {pictured} demo items got photos (images in {imagesRoot}).");
    }

    // Extra auctions in a time window, keeping all existing data:
    //   --window 2026-11-20T12:00-07:00 2026-11-20T18:00-07:00 [minutesBetween=10]
    var w = Array.IndexOf(args, "--window");
    if (w >= 0)
    {
        if (args.Length < w + 3
            || !DateTimeOffset.TryParse(args[w + 1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var from)
            || !DateTimeOffset.TryParse(args[w + 2], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var to))
        {
            Console.Error.WriteLine("Use: --window <from> <to> [minutesBetween], e.g. --window 2026-11-19T12:00-07:00 2026-11-19T18:00-07:00 10");
            return 1;
        }

        var minutes = args.Length > w + 3 && int.TryParse(args[w + 3], out var m) ? m : 10;
        var imagesRoot = Environment.GetEnvironmentVariable("Images__Path") ?? "/data/images";
        var catalog = new DemoCatalog(db, imagesRoot, DateTime.UtcNow);
        var added = await catalog.ScheduleWindowAsync(from.UtcDateTime, to.UtcDateTime, TimeSpan.FromMinutes(minutes));
        Console.WriteLine($"Window {from:yyyy-MM-dd HH:mm zzz} to {to:HH:mm zzz}: {added} new auctions, one every {minutes} min.");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Migration failed: {ex}");
    return 1;
}
