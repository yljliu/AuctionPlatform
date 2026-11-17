using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Auction.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` to create migrations.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<AuctionDbContext>
{
    public AuctionDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Sql")
                 ?? "Server=localhost,1433;Database=Auction;User Id=sa;Password=design-time-only;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<AuctionDbContext>().UseSqlServer(cs).Options;
        return new AuctionDbContext(options);
    }
}