using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    /// <summary>Spec 05 task 1: the one lease row the lifecycle leader takes. No model change.</summary>
    [DbContext(typeof(AuctionDbContext))]
    [Migration("20261119030000_LifecycleLeaseSeed")]
    public partial class LifecycleLeaseSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM bidding.LifecycleLeases WHERE LeaseName = 'lifecycle')
                    INSERT bidding.LifecycleLeases (LeaseName, Holder, FencingToken, ExpiresAt)
                    VALUES ('lifecycle', '', 0, '2000-01-01');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM bidding.LifecycleLeases WHERE LeaseName = 'lifecycle';");
        }
    }
}
