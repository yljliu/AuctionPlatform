using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Appeals",
                schema: "ops",
                columns: table => new
                {
                    AppealId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    AdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AdminNote = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appeals", x => x.AppealId);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "pay",
                columns: table => new
                {
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BuyerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", nullable: false),
                    Method = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CardLast4 = table.Column<string>(type: "char(4)", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.PaymentId);
                    table.ForeignKey(
                        name: "FK_Payments_Settlements_SettlementId",
                        column: x => x.SettlementId,
                        principalSchema: "pay",
                        principalTable: "Settlements",
                        principalColumn: "SettlementId");
                });

            migrationBuilder.CreateTable(
                name: "SecondChanceOffers",
                schema: "pay",
                columns: table => new
                {
                    OfferId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefaultedSettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BidderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: true),
                    NewSettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecondChanceOffers", x => x.OfferId);
                });

            migrationBuilder.CreateTable(
                name: "UserStrikes",
                schema: "ident",
                columns: table => new
                {
                    StrikeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    ClearedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserStrikes", x => x.StrikeId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Appeal_Queue",
                schema: "ops",
                table: "Appeals",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Appeal_Open",
                schema: "ops",
                table: "Appeals",
                column: "UserId",
                unique: true,
                filter: "[Status] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Payment_Idem",
                schema: "pay",
                table: "Payments",
                columns: new[] { "BuyerId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Payment_Settlement",
                schema: "pay",
                table: "Payments",
                column: "SettlementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Offer_Open",
                schema: "pay",
                table: "SecondChanceOffers",
                columns: new[] { "Status", "ExpiresAt" },
                filter: "[Status] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Offer_Default",
                schema: "pay",
                table: "SecondChanceOffers",
                column: "DefaultedSettlementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Strike_User",
                schema: "ident",
                table: "UserStrikes",
                columns: new[] { "UserId", "ClearedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Strike_Settlement",
                schema: "ident",
                table: "UserStrikes",
                column: "SettlementId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Appeals",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "Payments",
                schema: "pay");

            migrationBuilder.DropTable(
                name: "SecondChanceOffers",
                schema: "pay");

            migrationBuilder.DropTable(
                name: "UserStrikes",
                schema: "ident");
        }
    }
}
