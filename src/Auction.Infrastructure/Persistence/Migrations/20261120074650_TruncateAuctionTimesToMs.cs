using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TruncateAuctionTimesToMs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Cut existing auction times to whole milliseconds, as Redis has them (the model now saves them that way).
            // One statement, so CK_Times is checked on the new values together.
            migrationBuilder.Sql("""
                UPDATE catalog.Auctions SET
                    StartTime        = DATEADD(MICROSECOND, -(DATEPART(MICROSECOND, StartTime) % 1000), StartTime),
                    ScheduledEndTime = DATEADD(MICROSECOND, -(DATEPART(MICROSECOND, ScheduledEndTime) % 1000), ScheduledEndTime),
                    EndTime          = DATEADD(MICROSECOND, -(DATEPART(MICROSECOND, EndTime) % 1000), EndTime)
                WHERE DATEPART(MICROSECOND, StartTime) % 1000 <> 0
                   OR DATEPART(MICROSECOND, ScheduledEndTime) % 1000 <> 0
                   OR DATEPART(MICROSECOND, EndTime) % 1000 <> 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: whole-millisecond times are valid before and after.
        }
    }
}
