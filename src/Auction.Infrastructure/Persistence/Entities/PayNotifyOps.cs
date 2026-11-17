using Auction.Domain.Enums;

namespace Auction.Infrastructure.Persistence.Entities;

public class Settlement
{
    public Guid SettlementId { get; set; }
    public Guid AuctionId { get; set; }
    public Guid BuyerId { get; set; }
    public Guid SellerId { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal HammerPrice { get; set; }
    public decimal BuyerPremium { get; set; }
    public decimal ShippingCost { get; set; }
    public decimal Tax { get; set; }
    public decimal TotalDue { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal SellerFee { get; set; }
    public bool InstallmentsAllowed { get; set; }
    public Guid? ShippingAddressId { get; set; }
    public string? ShippingSnapshot { get; set; }
    public SettlementStatus Status { get; set; }
    public DateTime PaymentDeadline { get; set; }
    public bool IsSecondChance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class OutboxMessage
{
    public long OutboxId { get; set; }
    public string Topic { get; set; } = "";
    public string Key { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public class Notification
{
    public long NotificationId { get; set; }
    public Guid UserId { get; set; }
    public string EventKey { get; set; } = "";
    public NotificationType Type { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? Link { get; set; }
    public DateTime? ReadAt { get; set; }
    public byte? EmailStatus { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AuditLogEntry
{
    public long AuditId { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? Reason { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SimulationRun
{
    public Guid RunId { get; set; }
    public string Scenario { get; set; } = "";
    public string Config { get; set; } = "{}";
    public Guid StartedBy { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public SimulationStatus Status { get; set; }
    public string? Report { get; set; }
}
