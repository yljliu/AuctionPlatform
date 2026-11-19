using System.Text.Json;
using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Infrastructure.Bidding.Persistence;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Auction.Infrastructure.Notify;

/// <summary>A saved notification, as stored in SQL and pushed on Pub/Sub (u:{userId}).</summary>
public sealed record NotificationMessage(
    long NotificationId,
    Guid UserId,
    NotificationType Type,
    string Title,
    string? Body,
    string? Link,
    Guid? AuctionId,
    bool Read,
    long CreatedAtMs)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static NotificationMessage? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NotificationMessage>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"/a/{id}" -> id. Notification rows store the auction in the link only.</summary>
    public static Guid? AuctionOf(string? link) =>
        link != null && link.StartsWith("/a/", StringComparison.Ordinal) && Guid.TryParse(link[3..], out var id) ? id : null;
}

public sealed record NotificationPage(IReadOnlyList<NotificationMessage> Items, int Total, int Unread);

/// <summary>
/// SQL for notifications (spec 07 tasks 3 and 4). Saving uses one statement per event with OPENJSON, and
/// UX_Notif_Dedup (UserId, EventKey) plus NOT EXISTS turns repeats into nothing (R2, R4.1).
/// Only rows that were really added are published on Pub/Sub.
/// </summary>
public sealed class NotificationStore(SqlConnectionFactory connections, IConnectionMultiplexer redis, IMemoryCache cache)
{
    public static string Channel(Guid userId) => $"u:{userId}";

    private sealed class Row
    {
        public long NotificationId { get; set; }
        public Guid UserId { get; set; }
        public short Type { get; set; }
        public string Title { get; set; } = "";
        public string? Body { get; set; }
        public string? Link { get; set; }
        public DateTime? ReadAt { get; set; }
        public DateTime CreatedAt { get; set; }

        public NotificationMessage ToMessage(Guid? auctionId = null) => new(NotificationId, UserId, (NotificationType)Type, Title, Body, Link,
            auctionId ?? NotificationMessage.AuctionOf(Link), ReadAt != null, new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
    }

    private const string Insert = """
        INSERT notify.Notifications (UserId, EventKey, Type, Title, Body, Link, CreatedAt)
        OUTPUT inserted.NotificationId, inserted.UserId, inserted.Type, inserted.Title, inserted.Body, inserted.Link, inserted.ReadAt, inserted.CreatedAt
        SELECT j.UserId, j.EventKey, j.Type, j.Title, j.Body, j.Link, SYSUTCDATETIME()
        FROM OPENJSON(@Rows) WITH (UserId uniqueidentifier, EventKey varchar(128), Type smallint, Title nvarchar(200), Body nvarchar(1000), Link varchar(300)) j
        WHERE NOT EXISTS (SELECT 1 FROM notify.Notifications n WITH (UPDLOCK, HOLDLOCK) WHERE n.UserId = j.UserId AND n.EventKey = j.EventKey);
        """;

    /// <summary>Saves the drafts that are new and pushes each one to its user's channel. Returns the new ones.</summary>
    public async Task<IReadOnlyList<NotificationMessage>> SaveAndPublishAsync(IReadOnlyList<NotificationDraft> drafts, CancellationToken ct = default)
    {
        if (drafts.Count == 0)
        {
            return [];
        }

        // One draft per (user, key) inside a batch too.
        var unique = drafts.GroupBy(d => (d.UserId, d.EventKey)).Select(g => g.First()).ToList();
        var json = JsonSerializer.Serialize(unique.Select(d => new
        {
            d.UserId, d.EventKey, Type = (short)d.Type, Title = Cut(d.Title, 200), Body = d.Body == null ? null : Cut(d.Body, 1000), d.Link,
        }));

        IReadOnlyList<NotificationMessage> added;
        await using (var connection = connections.Create())
        {
            try
            {
                added = (await connection.QueryAsync<Row>(new CommandDefinition(Insert, new { Rows = json }, cancellationToken: ct)))
                    .Select(r => r.ToMessage(unique[0].AuctionId)).ToList();   // one event = one auction
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
            {
                added = [];   // another copy saved the same event at the same moment
            }
        }

        // A missed push only delays the bell; the row is already saved (R3.2).
        var subscriber = redis.GetSubscriber();
        foreach (var message in added)
        {
            try
            {
                await subscriber.PublishAsync(RedisChannel.Literal(Channel(message.UserId)), message.ToJson());
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                break;
            }
        }

        return added;
    }

    /// <summary>Watchers and bidders of one auction, read fresh (events that need them are rare).</summary>
    public async Task<Recipients> RecipientsAsync(Guid auctionId, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        using var results = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT UserId FROM bidding.Watchlist WHERE AuctionId = @Id;
            SELECT DISTINCT UserId FROM bidding.Bids WHERE AuctionId = @Id AND VoidedAt IS NULL;
            """, new { Id = auctionId }, cancellationToken: ct));
        var watchers = (await results.ReadAsync<Guid>()).ToHashSet();
        var bidders = (await results.ReadAsync<Guid>()).ToHashSet();
        return new Recipients(watchers, bidders);
    }

    /// <summary>Title, seller and currency, cached 1 minute (design 2). Null when the auction is gone.</summary>
    public async Task<AuctionInfo?> AuctionAsync(Guid auctionId, CancellationToken ct = default)
    {
        var key = "notify:auction:" + auctionId;
        if (cache.TryGetValue(key, out AuctionInfo? info))
        {
            return info;
        }

        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<AuctionRow>(new CommandDefinition("""
            SELECT a.AuctionId, i.Title, a.SellerId, a.Currency
            FROM catalog.Auctions a JOIN catalog.Items i ON i.ItemId = a.ItemId WHERE a.AuctionId = @Id;
            """, new { Id = auctionId }, cancellationToken: ct));
        info = row == null ? null : new AuctionInfo(row.AuctionId, row.Title, row.SellerId, row.Currency);
        if (info != null)
        {
            cache.Set(key, info, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1), Size = 1 });
        }

        return info;
    }

    private sealed class AuctionRow
    {
        public Guid AuctionId { get; set; }
        public string Title { get; set; } = "";
        public Guid SellerId { get; set; }
        public string Currency { get; set; } = "USD";
    }

    // ---------- reads for NotificationService (task 4) ----------

    public async Task<NotificationPage> ListAsync(Guid userId, bool unreadOnly, Paging paging, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        using var results = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT NotificationId, UserId, Type, Title, Body, Link, ReadAt, CreatedAt
            FROM notify.Notifications
            WHERE UserId = @UserId AND (@UnreadOnly = 0 OR ReadAt IS NULL)
            ORDER BY CreatedAt DESC, NotificationId DESC
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;

            SELECT COUNT(*) FROM notify.Notifications WHERE UserId = @UserId AND (@UnreadOnly = 0 OR ReadAt IS NULL);
            SELECT COUNT(*) FROM notify.Notifications WHERE UserId = @UserId AND ReadAt IS NULL;
            """, new { UserId = userId, UnreadOnly = unreadOnly, paging.Skip, Take = paging.Size }, cancellationToken: ct));
        var items = (await results.ReadAsync<Row>()).Select(r => r.ToMessage()).ToList();
        var total = await results.ReadSingleAsync<int>();
        var unread = await results.ReadSingleAsync<int>();
        return new NotificationPage(items, total, unread);
    }

    /// <summary>Marks these (only the caller's own) as read. Returns the unread count after.</summary>
    public async Task<int> MarkReadAsync(Guid userId, IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        if (ids.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE notify.Notifications SET ReadAt = SYSUTCDATETIME() WHERE UserId = @UserId AND ReadAt IS NULL AND NotificationId IN @Ids;",
                new { UserId = userId, Ids = ids.Take(500).ToArray() }, cancellationToken: ct));
        }

        return await UnreadAsync(connection, userId, ct);
    }

    public async Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE notify.Notifications SET ReadAt = SYSUTCDATETIME() WHERE UserId = @UserId AND ReadAt IS NULL;",
            new { UserId = userId }, cancellationToken: ct));
        return 0;
    }

    public async Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default)
    {
        await using var connection = connections.Create();
        return await UnreadAsync(connection, userId, ct);
    }

    private static Task<int> UnreadAsync(System.Data.IDbConnection connection, Guid userId, CancellationToken ct) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM notify.Notifications WHERE UserId = @UserId AND ReadAt IS NULL;", new { UserId = userId }, cancellationToken: ct));

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
