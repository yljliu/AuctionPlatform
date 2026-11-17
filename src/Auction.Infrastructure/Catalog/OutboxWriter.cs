using System.Text.Json;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;

namespace Auction.Infrastructure.Catalog;

/// <summary>Outbox topics written by the catalog.</summary>
public static class OutboxTopics
{
    public const string AuctionCreated = "AuctionCreated";
    public const string AuctionUpdated = "AuctionUpdated";
    public const string AuctionCancelled = "AuctionCancelled";
}

/// <summary>
/// Adds a notify.Outbox row to the current DbContext. It is saved by the same SaveChanges as
/// the business change, so both are in one transaction: either both are saved or neither.
/// The outbox relay (spec 05/07) publishes the rows later.
/// </summary>
public sealed class OutboxWriter(AuctionDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Add(string topic, string key, object payload)
    {
        db.Outbox.Add(new OutboxMessage
        {
            Topic = topic,
            Key = key,
            Payload = JsonSerializer.Serialize(payload, Json),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        });
    }
}
