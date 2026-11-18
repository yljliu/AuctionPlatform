using Auction.Domain.Enums;

namespace Auction.Infrastructure.Persistence.Entities;

public class Bid
{
    public long BidId { get; set; }
    public Guid AuctionId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Stream entry number (eseq) the row came from. With Idx it makes each row unique.</summary>
    public long ESeq { get; set; }

    /// <summary>Position of the row inside its stream entry: 0, 1, ...</summary>
    public byte Idx { get; set; }

    /// <summary>Public event number. Null for MaxRaise rows, which are not public events.</summary>
    public long? Seq { get; set; }

    public BidKind Kind { get; set; }
    public decimal Amount { get; set; }
    public decimal? MaxAmount { get; set; }
    public Guid? IdempotencyKey { get; set; }
    public DateTime AcceptedAt { get; set; }
    public DateTime? VoidedAt { get; set; }
    public Guid? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
}

public class AuctionEvent
{
    public long EventId { get; set; }
    public Guid AuctionId { get; set; }
    public long Seq { get; set; }
    public long ESeq { get; set; }
    public short EventType { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTime OccurredAt { get; set; }
}

public class LifecycleLease
{
    public string LeaseName { get; set; } = "";
    public string Holder { get; set; } = "";
    public long FencingToken { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class WatchlistEntry
{
    public Guid UserId { get; set; }
    public Guid AuctionId { get; set; }
    public DateTime CreatedAt { get; set; }
}
