using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BidEseq : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Bids_Seq",
                schema: "bidding",
                table: "Bids");

            migrationBuilder.DropColumn(
                name: "SubSeq",
                schema: "bidding",
                table: "Bids");

            migrationBuilder.AlterColumn<long>(
                name: "Seq",
                schema: "bidding",
                table: "Bids",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "ESeq",
                schema: "bidding",
                table: "Bids",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte>(
                name: "Idx",
                schema: "bidding",
                table: "Bids",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<long>(
                name: "LastESeq",
                schema: "catalog",
                table: "Auctions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ESeq",
                schema: "bidding",
                table: "AuctionEvents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_Bids_Seq",
                schema: "bidding",
                table: "Bids",
                columns: new[] { "AuctionId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "UX_Bids_ESeq",
                schema: "bidding",
                table: "Bids",
                columns: new[] { "AuctionId", "ESeq", "Idx" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bids_Seq",
                schema: "bidding",
                table: "Bids");

            migrationBuilder.DropIndex(
                name: "UX_Bids_ESeq",
                schema: "bidding",
                table: "Bids");

            migrationBuilder.DropColumn(
                name: "ESeq",
                schema: "bidding",
                table: "Bids");

            migrationBuilder.DropColumn(
                name: "Idx",
                schema: "bidding",
                table: "Bids");

            migrationBuilder.DropColumn(
                name: "LastESeq",
                schema: "catalog",
                table: "Auctions");

            migrationBuilder.DropColumn(
                name: "ESeq",
                schema: "bidding",
                table: "AuctionEvents");

            migrationBuilder.AlterColumn<long>(
                name: "Seq",
                schema: "bidding",
                table: "Bids",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "SubSeq",
                schema: "bidding",
                table: "Bids",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateIndex(
                name: "UX_Bids_Seq",
                schema: "bidding",
                table: "Bids",
                columns: new[] { "AuctionId", "Seq", "SubSeq" },
                unique: true);
        }
    }
}
