using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "bidding");

            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.EnsureSchema(
                name: "ops");

            migrationBuilder.EnsureSchema(
                name: "notify");

            migrationBuilder.EnsureSchema(
                name: "ident");

            migrationBuilder.EnsureSchema(
                name: "pay");

            migrationBuilder.CreateTable(
                name: "AuctionEvents",
                schema: "bidding",
                columns: table => new
                {
                    EventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AuctionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<short>(type: "smallint", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuctionEvents", x => x.EventId);
                    table.CheckConstraint("CK_Events_Json", "ISJSON([Payload]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                schema: "ops",
                columns: table => new
                {
                    AuditId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    TargetId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Before = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    After = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.AuditId);
                });

            migrationBuilder.CreateTable(
                name: "BidIncrementTiers",
                schema: "catalog",
                columns: table => new
                {
                    TableId = table.Column<int>(type: "int", nullable: false),
                    FromPrice = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Increment = table.Column<decimal>(type: "decimal(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BidIncrementTiers", x => new { x.TableId, x.FromPrice });
                });

            migrationBuilder.CreateTable(
                name: "Bids",
                schema: "bidding",
                columns: table => new
                {
                    BidId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AuctionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    SubSeq = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    MaxAmount = table.Column<decimal>(type: "decimal(19,4)", nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AcceptedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    VoidedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: true),
                    VoidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bids", x => x.BidId);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                schema: "catalog",
                columns: table => new
                {
                    CategoryId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParentCategoryId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.CategoryId);
                    table.ForeignKey(
                        name: "FK_Categories_Categories_ParentCategoryId",
                        column: x => x.ParentCategoryId,
                        principalSchema: "catalog",
                        principalTable: "Categories",
                        principalColumn: "CategoryId");
                });

            migrationBuilder.CreateTable(
                name: "FeeSchedules",
                schema: "catalog",
                columns: table => new
                {
                    FeeScheduleId = table.Column<int>(type: "int", nullable: false),
                    BuyerPremiumPct = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    SellerFinalValuePct = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    ListingFee = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeeSchedules", x => x.FeeScheduleId);
                });

            migrationBuilder.CreateTable(
                name: "LifecycleLeases",
                schema: "bidding",
                columns: table => new
                {
                    LeaseName = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Holder = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleLeases", x => x.LeaseName);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "notify",
                columns: table => new
                {
                    NotificationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventKey = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    Type = table.Column<short>(type: "smallint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Link = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    ReadAt = table.Column<DateTime>(type: "datetime2(6)", nullable: true),
                    EmailStatus = table.Column<byte>(type: "tinyint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.NotificationId);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                schema: "notify",
                columns: table => new
                {
                    OutboxId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Topic = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.OutboxId);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                schema: "ident",
                columns: table => new
                {
                    TokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: true),
                    ReplacedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    UserAgent = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Ip = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.TokenId);
                });

            migrationBuilder.CreateTable(
                name: "Settlements",
                schema: "pay",
                columns: table => new
                {
                    SettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BuyerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", nullable: false),
                    HammerPrice = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    BuyerPremium = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    ShippingCost = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    TotalDue = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    AmountPaid = table.Column<decimal>(type: "decimal(19,4)", nullable: false, defaultValue: 0m),
                    SellerFee = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    InstallmentsAllowed = table.Column<bool>(type: "bit", nullable: false),
                    ShippingAddressId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ShippingSnapshot = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    PaymentDeadline = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    IsSecondChance = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settlements", x => x.SettlementId);
                });

            migrationBuilder.CreateTable(
                name: "SimulationRuns",
                schema: "ops",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scenario = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Config = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Report = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimulationRuns", x => x.RunId);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "ident",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    Username = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    EmailNormalized = table.Column<string>(type: "nvarchar(450)", nullable: false, computedColumnSql: "UPPER([Email])", stored: true),
                    EmailVerifiedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: true),
                    PasswordHash = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: false),
                    PhoneNumber = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    TotpSecretEnc = table.Column<byte[]>(type: "varbinary(256)", maxLength: 256, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    SuspendedUntil = table.Column<DateTime>(type: "datetime2(6)", nullable: true),
                    StatusVersion = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    FailedLogins = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    LockedUntil = table.Column<DateTime>(type: "datetime2(6)", nullable: true),
                    IsBot = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Watchlist",
                schema: "bidding",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watchlist", x => new { x.UserId, x.AuctionId });
                });

            migrationBuilder.CreateTable(
                name: "Items",
                schema: "catalog",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Condition = table.Column<byte>(type: "tinyint", nullable: false),
                    WarrantyType = table.Column<byte>(type: "tinyint", nullable: false),
                    WarrantyNote = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ShippingType = table.Column<byte>(type: "tinyint", nullable: false),
                    ShippingCost = table.Column<decimal>(type: "decimal(19,4)", nullable: true),
                    HandlingDays = table.Column<byte>(type: "tinyint", nullable: false),
                    ReturnsAccepted = table.Column<bool>(type: "bit", nullable: false),
                    ReturnWindowDays = table.Column<byte>(type: "tinyint", nullable: true),
                    ReturnShippingPaidBy = table.Column<byte>(type: "tinyint", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.ItemId);
                    table.CheckConstraint("CK_Returns", "[ReturnsAccepted] = 0 OR [ReturnWindowDays] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_Items_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "catalog",
                        principalTable: "Categories",
                        principalColumn: "CategoryId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SellerProfiles",
                schema: "ident",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: true),
                    VerifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RejectReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RatingCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    RatingSum = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    CompletedSales = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SellerProfiles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_SellerProfiles_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "ident",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                schema: "ident",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<byte>(type: "tinyint", nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    GrantedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.Role });
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "ident",
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Auctions",
                schema: "catalog",
                columns: table => new
                {
                    AuctionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", nullable: false, defaultValue: "USD"),
                    StartTime = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    ScheduledEndTime = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    EndTime = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    StartingPrice = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    ReservePrice = table.Column<decimal>(type: "decimal(19,4)", nullable: true),
                    BuyNowPrice = table.Column<decimal>(type: "decimal(19,4)", nullable: true),
                    IncrementTableId = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    FeeScheduleId = table.Column<int>(type: "int", nullable: false),
                    SnipeWindowSec = table.Column<int>(type: "int", nullable: false, defaultValue: 60),
                    ExtensionSec = table.Column<int>(type: "int", nullable: false, defaultValue: 60),
                    MaxTotalExtensionSec = table.Column<int>(type: "int", nullable: false, defaultValue: 3600),
                    InstallmentsAllowed = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Outcome = table.Column<byte>(type: "tinyint", nullable: true),
                    CurrentPrice = table.Column<decimal>(type: "decimal(19,4)", nullable: true),
                    CurrentLeaderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BidCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    LastSeq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    CancelledReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CancelledBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Auctions", x => x.AuctionId);
                    table.CheckConstraint("CK_Prices", "[StartingPrice] > 0 AND ([ReservePrice] IS NULL OR [ReservePrice] >= [StartingPrice])");
                    table.CheckConstraint("CK_Times", "[StartTime] < [ScheduledEndTime] AND [EndTime] >= [ScheduledEndTime]");
                    table.ForeignKey(
                        name: "FK_Auctions_Items_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "catalog",
                        principalTable: "Items",
                        principalColumn: "ItemId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ItemImages",
                schema: "catalog",
                columns: table => new
                {
                    ImageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageKey = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Bytes = table.Column<int>(type: "int", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemImages", x => x.ImageId);
                    table.ForeignKey(
                        name: "FK_ItemImages_Items_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "catalog",
                        principalTable: "Items",
                        principalColumn: "ItemId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Events_Seq",
                schema: "bidding",
                table: "AuctionEvents",
                columns: new[] { "AuctionId", "Seq", "EventType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_Browse",
                schema: "catalog",
                table: "Auctions",
                columns: new[] { "Status", "EndTime" })
                .Annotation("SqlServer:Include", new[] { "CurrentPrice", "BidCount", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_Seller",
                schema: "catalog",
                table: "Auctions",
                columns: new[] { "SellerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_Start",
                schema: "catalog",
                table: "Auctions",
                column: "StartTime",
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_OneOpenAuctionPerItem",
                schema: "catalog",
                table: "Auctions",
                column: "ItemId",
                unique: true,
                filter: "[Status] IN (1,2,3,4,7)");

            migrationBuilder.CreateIndex(
                name: "IX_Bids_User",
                schema: "bidding",
                table: "Bids",
                columns: new[] { "UserId", "AuctionId" })
                .Annotation("SqlServer:Include", new[] { "Amount", "MaxAmount" });

            migrationBuilder.CreateIndex(
                name: "IX_Bids_Winner",
                schema: "bidding",
                table: "Bids",
                columns: new[] { "AuctionId", "Amount" },
                descending: new[] { false, true },
                filter: "[VoidedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Bids_Idem",
                schema: "bidding",
                table: "Bids",
                columns: new[] { "AuctionId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Bids_Seq",
                schema: "bidding",
                table: "Bids",
                columns: new[] { "AuctionId", "Seq", "SubSeq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_ParentCategoryId",
                schema: "catalog",
                table: "Categories",
                column: "ParentCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Slug",
                schema: "catalog",
                table: "Categories",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemImages_ItemId",
                schema: "catalog",
                table: "ItemImages",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_CategoryId",
                schema: "catalog",
                table: "Items",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_SellerId_Status",
                schema: "catalog",
                table: "Items",
                columns: new[] { "SellerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Notif_Unread",
                schema: "notify",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAt" },
                descending: new[] { false, true },
                filter: "[ReadAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Notif_Dedup",
                schema: "notify",
                table: "Notifications",
                columns: new[] { "UserId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Unpub",
                schema: "notify",
                table: "Outbox",
                column: "OutboxId",
                filter: "[PublishedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_FamilyId",
                schema: "ident",
                table: "RefreshTokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                schema: "ident",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Settlement_Active",
                schema: "pay",
                table: "Settlements",
                column: "AuctionId",
                unique: true,
                filter: "[Status] IN (0,1,2,3,4,7)");

            migrationBuilder.CreateIndex(
                name: "IX_Users_EmailNormalized",
                schema: "ident",
                table: "Users",
                column: "EmailNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                schema: "ident",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Watch_Auction",
                schema: "bidding",
                table: "Watchlist",
                column: "AuctionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuctionEvents",
                schema: "bidding");

            migrationBuilder.DropTable(
                name: "Auctions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "AuditLog",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "BidIncrementTiers",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Bids",
                schema: "bidding");

            migrationBuilder.DropTable(
                name: "FeeSchedules",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ItemImages",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "LifecycleLeases",
                schema: "bidding");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "Outbox",
                schema: "notify");

            migrationBuilder.DropTable(
                name: "RefreshTokens",
                schema: "ident");

            migrationBuilder.DropTable(
                name: "SellerProfiles",
                schema: "ident");

            migrationBuilder.DropTable(
                name: "Settlements",
                schema: "pay");

            migrationBuilder.DropTable(
                name: "SimulationRuns",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "UserRoles",
                schema: "ident");

            migrationBuilder.DropTable(
                name: "Watchlist",
                schema: "bidding");

            migrationBuilder.DropTable(
                name: "Items",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "ident");

            migrationBuilder.DropTable(
                name: "Categories",
                schema: "catalog");
        }
    }
}
