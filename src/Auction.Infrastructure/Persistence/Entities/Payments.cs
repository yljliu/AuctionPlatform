namespace Auction.Infrastructure.Persistence.Entities;

/// <summary>One fake card payment (spec 10). Only the last 4 digits of the card are kept.</summary>
public class Payment
{
    public Guid PaymentId { get; set; }
    public Guid SettlementId { get; set; }
    public Guid BuyerId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Method { get; set; } = "DemoCard";
    public string CardLast4 { get; set; } = "";
    public Guid IdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A strike for not paying (spec 10 R5, design W12). Active while ClearedAt is null.</summary>
public class UserStrike
{
    public long StrikeId { get; set; }
    public Guid UserId { get; set; }
    public Guid SettlementId { get; set; }
    public string Reason { get; set; } = "NonPayment";
    public DateTime CreatedAt { get; set; }
    public DateTime? ClearedAt { get; set; }
}

public enum SecondChanceStatus : byte
{
    Open = 0,
    Accepted = 1,
    Declined = 2,
    Expired = 3,
}

/// <summary>The seller's offer to the next bidder after a default (spec 10 R6).</summary>
public class SecondChanceOffer
{
    public Guid OfferId { get; set; }
    public Guid DefaultedSettlementId { get; set; }
    public Guid AuctionId { get; set; }
    public Guid SellerId { get; set; }
    public Guid BidderId { get; set; }
    public decimal Amount { get; set; }
    public SecondChanceStatus Status { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public Guid? NewSettlementId { get; set; }
}

public enum AppealStatus : byte
{
    Open = 0,
    Accepted = 1,
    Rejected = 2,
}

/// <summary>A suspended user's appeal to the admins (spec 10 R7).</summary>
public class Appeal
{
    public long AppealId { get; set; }
    public Guid UserId { get; set; }
    public string Message { get; set; } = "";
    public AppealStatus Status { get; set; }
    public Guid? AdminId { get; set; }
    public string? AdminNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
