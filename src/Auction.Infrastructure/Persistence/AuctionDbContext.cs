using Auction.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using AuctionEntity = Auction.Infrastructure.Persistence.Entities.Auction;

namespace Auction.Infrastructure.Persistence;

public class AuctionDbContext(DbContextOptions<AuctionDbContext> options) : DbContext(options)
{
    // ident
    public DbSet<User> Users => Set<User>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SellerProfile> SellerProfiles => Set<SellerProfile>();
    // catalog
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ItemImage> ItemImages => Set<ItemImage>();
    public DbSet<BidIncrementTier> BidIncrementTiers => Set<BidIncrementTier>();
    public DbSet<FeeSchedule> FeeSchedules => Set<FeeSchedule>();
    public DbSet<AuctionEntity> Auctions => Set<AuctionEntity>();
    // bidding
    public DbSet<Bid> Bids => Set<Bid>();
    public DbSet<AuctionEvent> AuctionEvents => Set<AuctionEvent>();
    public DbSet<LifecycleLease> LifecycleLeases => Set<LifecycleLease>();
    public DbSet<WatchlistEntry> Watchlist => Set<WatchlistEntry>();
    // pay, notify, ops
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<SimulationRun> SimulationRuns => Set<SimulationRun>();

    private const string Money = "decimal(19,4)";
    private const string Time = "datetime2(6)";

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HaveColumnType(Money);
        builder.Properties<DateTime>().HaveColumnType(Time);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        ConfigureIdent(b);
        ConfigureCatalog(b);
        ConfigureBidding(b);
        ConfigurePayNotifyOps(b);
    }

    private static void ConfigureIdent(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("Users", "ident");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(x => x.Username).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Email).HasMaxLength(254).IsRequired();
            e.Property(x => x.EmailNormalized).HasComputedColumnSql("UPPER([Email])", stored: true);
            e.HasIndex(x => x.EmailNormalized).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(255).IsUnicode(false).IsRequired();
            e.Property(x => x.PhoneNumber).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.TotpSecretEnc).HasMaxLength(256);
            e.Property(x => x.StatusVersion).HasDefaultValue(0).HasSentinel(int.MinValue);
            e.Property(x => x.FailedLogins).HasDefaultValue(0).HasSentinel(int.MinValue);
            e.Property(x => x.IsBot).HasDefaultValue(false);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.UserId);
        });

        b.Entity<UserRole>(e =>
        {
            e.ToTable("UserRoles", "ident");
            e.HasKey(x => new { x.UserId, x.Role });
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens", "ident");
            e.HasKey(x => x.TokenId);
            e.Property(x => x.TokenId).ValueGeneratedNever();
            e.Property(x => x.TokenHash).HasColumnType("binary(32)");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.Property(x => x.UserAgent).HasMaxLength(200);
            e.Property(x => x.Ip).HasMaxLength(45).IsUnicode(false);
        });

        b.Entity<SellerProfile>(e =>
        {
            e.ToTable("SellerProfiles", "ident");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
            e.Property(x => x.DisplayName).HasMaxLength(64).IsRequired();
            e.Property(x => x.RejectReason).HasMaxLength(400);
            e.Property(x => x.RatingCount).HasDefaultValue(0).HasSentinel(int.MinValue);
            e.Property(x => x.RatingSum).HasDefaultValue(0).HasSentinel(int.MinValue);
            e.Property(x => x.CompletedSales).HasDefaultValue(0).HasSentinel(int.MinValue);
            e.HasOne<User>().WithOne().HasForeignKey<SellerProfile>(x => x.UserId);
        });
    }

    private static void ConfigureCatalog(ModelBuilder b)
    {
        b.Entity<Category>(e =>
        {
            e.ToTable("Categories", "catalog");
            e.HasKey(x => x.CategoryId);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(100).IsUnicode(false).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.SortOrder).HasDefaultValue(0).HasSentinel(int.MinValue);
            e.Property(x => x.IsActive).HasDefaultValue(true).HasSentinel(true);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.ParentCategoryId);
        });

        b.Entity<Item>(e =>
        {
            e.ToTable("Items", "catalog", t => t.HasCheckConstraint("CK_Returns", "[ReturnsAccepted] = 0 OR [ReturnWindowDays] IS NOT NULL"));
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(120).IsRequired();
            e.Property(x => x.Description).IsRequired();   // nvarchar(max)
            e.Property(x => x.WarrantyNote).HasMaxLength(400);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId);
            e.HasIndex(x => new { x.SellerId, x.Status });
        });

        b.Entity<ItemImage>(e =>
        {
            e.ToTable("ItemImages", "catalog");
            e.HasKey(x => x.ImageId);
            e.Property(x => x.ImageId).ValueGeneratedNever();
            e.Property(x => x.StorageKey).HasMaxLength(300).IsUnicode(false).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(50).IsUnicode(false).IsRequired();
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId);
        });

        b.Entity<BidIncrementTier>(e =>
        {
            e.ToTable("BidIncrementTiers", "catalog");
            e.HasKey(x => new { x.TableId, x.FromPrice });
        });

        b.Entity<FeeSchedule>(e =>
        {
            e.ToTable("FeeSchedules", "catalog");
            e.HasKey(x => x.FeeScheduleId);
            e.Property(x => x.FeeScheduleId).ValueGeneratedNever();
            e.Property(x => x.BuyerPremiumPct).HasColumnType("decimal(5,2)");
            e.Property(x => x.SellerFinalValuePct).HasColumnType("decimal(5,2)");
        });

        b.Entity<AuctionEntity>(e =>
        {
            e.ToTable("Auctions", "catalog", t =>
            {
                t.HasCheckConstraint("CK_Times", "[StartTime] < [ScheduledEndTime] AND [EndTime] >= [ScheduledEndTime]");
                t.HasCheckConstraint("CK_Prices", "[StartingPrice] > 0 AND ([ReservePrice] IS NULL OR [ReservePrice] >= [StartingPrice])");
            });
            e.HasKey(x => x.AuctionId);
            e.Property(x => x.AuctionId).ValueGeneratedNever();
            e.Property(x => x.Currency).HasColumnType("char(3)").IsRequired().HasDefaultValue("USD");
            e.Property(x => x.IncrementTableId).HasDefaultValue(1).HasSentinel(int.MinValue);
            e.Property(x => x.SnipeWindowSec).HasDefaultValue(60).HasSentinel(int.MinValue);
            e.Property(x => x.ExtensionSec).HasDefaultValue(60).HasSentinel(int.MinValue);
            e.Property(x => x.MaxTotalExtensionSec).HasDefaultValue(3600).HasSentinel(int.MinValue);
            e.Property(x => x.InstallmentsAllowed).HasDefaultValue(false);
            e.Property(x => x.BidCount).HasDefaultValue(0).HasSentinel(int.MinValue);
            e.Property(x => x.LastSeq).HasDefaultValue(0L).HasSentinel(long.MinValue);
            e.Property(x => x.CancelledReason).HasMaxLength(400);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId);
            // One open auction per item: Scheduled(1), Live(2), Sealed(3), Settling(4), Frozen(7)
            e.HasIndex(x => x.ItemId).IsUnique().HasDatabaseName("UX_OneOpenAuctionPerItem")
                .HasFilter("[Status] IN (1,2,3,4,7)");
            e.HasIndex(x => new { x.Status, x.EndTime }).HasDatabaseName("IX_Auctions_Browse")
                .IncludeProperties(x => new { x.CurrentPrice, x.BidCount, x.ItemId });
            e.HasIndex(x => new { x.SellerId, x.Status }).HasDatabaseName("IX_Auctions_Seller");
            e.HasIndex(x => x.StartTime).HasDatabaseName("IX_Auctions_Start").HasFilter("[Status] = 1");
        });
    }

    private static void ConfigureBidding(ModelBuilder b)
    {
        b.Entity<Bid>(e =>
        {
            e.ToTable("Bids", "bidding");
            e.HasKey(x => x.BidId);
            e.Property(x => x.VoidReason).HasMaxLength(400);
            e.Property(x => x.SubSeq).HasDefaultValue((byte)0).HasSentinel(byte.MaxValue);
            e.HasIndex(x => new { x.AuctionId, x.Seq, x.SubSeq }).IsUnique().HasDatabaseName("UX_Bids_Seq");
            e.HasIndex(x => new { x.AuctionId, x.IdempotencyKey }).IsUnique().HasDatabaseName("UX_Bids_Idem")
                .HasFilter("[IdempotencyKey] IS NOT NULL");
            e.HasIndex(x => new { x.UserId, x.AuctionId }).HasDatabaseName("IX_Bids_User")
                .IncludeProperties(x => new { x.Amount, x.MaxAmount });
            e.HasIndex(x => new { x.AuctionId, x.Amount }).IsDescending(false, true).HasDatabaseName("IX_Bids_Winner")
                .HasFilter("[VoidedAt] IS NULL");
        });

        b.Entity<AuctionEvent>(e =>
        {
            e.ToTable("AuctionEvents", "bidding", t => t.HasCheckConstraint("CK_Events_Json", "ISJSON([Payload]) = 1"));
            e.HasKey(x => x.EventId);
            e.HasIndex(x => new { x.AuctionId, x.Seq, x.EventType }).IsUnique().HasDatabaseName("UX_Events_Seq");
        });

        b.Entity<LifecycleLease>(e =>
        {
            e.ToTable("LifecycleLeases", "bidding");
            e.HasKey(x => x.LeaseName);
            e.Property(x => x.LeaseName).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.Holder).HasMaxLength(128).IsUnicode(false).IsRequired();
        });

        b.Entity<WatchlistEntry>(e =>
        {
            e.ToTable("Watchlist", "bidding");
            e.HasKey(x => new { x.UserId, x.AuctionId });
            e.HasIndex(x => x.AuctionId).HasDatabaseName("IX_Watch_Auction");
        });
    }

    private static void ConfigurePayNotifyOps(ModelBuilder b)
    {
        b.Entity<Settlement>(e =>
        {
            e.ToTable("Settlements", "pay");
            e.HasKey(x => x.SettlementId);
            e.Property(x => x.SettlementId).ValueGeneratedNever();
            e.Property(x => x.Currency).HasColumnType("char(3)").IsRequired();
            e.Property(x => x.ShippingSnapshot).HasMaxLength(1000);
            e.Property(x => x.AmountPaid).HasDefaultValue(0m).HasSentinel(decimal.MinValue);
            e.Property(x => x.IsSecondChance).HasDefaultValue(false);
            e.Property(x => x.RowVersion).IsRowVersion();
            // one live obligation per auction: every status except Defaulted(5) and Refunded(6). SQL Server filtered indexes do not allow NOT IN.
            e.HasIndex(x => x.AuctionId).IsUnique().HasDatabaseName("UX_Settlement_Active")
                .HasFilter("[Status] IN (0,1,2,3,4,7)");
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("Outbox", "notify");
            e.HasKey(x => x.OutboxId);
            e.Property(x => x.Topic).HasMaxLength(64).IsUnicode(false).IsRequired();
            e.Property(x => x.Key).HasMaxLength(128).IsUnicode(false).IsRequired();
            e.HasIndex(x => x.OutboxId).HasDatabaseName("IX_Outbox_Unpub").HasFilter("[PublishedAt] IS NULL");
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("Notifications", "notify");
            e.HasKey(x => x.NotificationId);
            e.Property(x => x.EventKey).HasMaxLength(128).IsUnicode(false).IsRequired();
            e.Property(x => x.Type).HasConversion<short>();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Body).HasMaxLength(1000);
            e.Property(x => x.Link).HasMaxLength(300).IsUnicode(false);
            e.HasIndex(x => new { x.UserId, x.EventKey }).IsUnique().HasDatabaseName("UX_Notif_Dedup");
            e.HasIndex(x => new { x.UserId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("IX_Notif_Unread")
                .HasFilter("[ReadAt] IS NULL");
        });

        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLog", "ops");
            e.HasKey(x => x.AuditId);
            e.Property(x => x.Action).HasMaxLength(64).IsUnicode(false).IsRequired();
            e.Property(x => x.TargetType).HasMaxLength(32).IsUnicode(false).IsRequired();
            e.Property(x => x.TargetId).HasMaxLength(64).IsUnicode(false).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(400);
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
        });

        b.Entity<SimulationRun>(e =>
        {
            e.ToTable("SimulationRuns", "ops");
            e.HasKey(x => x.RunId);
            e.Property(x => x.RunId).ValueGeneratedNever();
            e.Property(x => x.Scenario).HasMaxLength(32).IsUnicode(false).IsRequired();
        });
    }
}
