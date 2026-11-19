using System.Globalization;
using System.Text.Json;
using Auction.Contracts;
using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Auction.Infrastructure.Live;

/// <summary>Everything a snapshot needs, from Redis or from SQL. Holds no max; the reserve only decides reserve_met.</summary>
public sealed record SnapshotInput(
    Guid AuctionId,
    string Status,
    long Seq,
    long PriceC,
    long StartC,
    int BidCount,
    Guid? Leader,
    long EndMs,
    long ExtUsedMs,
    long? ReserveC,
    string Inc,
    string Currency,
    int Watchers,
    IReadOnlyList<PublicBidJson> Bids,
    long ServerTsMs);

/// <summary>Builds the AuctionSnapshot message (spec 04 task 3). Pure: no Redis, no SQL.</summary>
public static class SnapshotMapper
{
    public static AuctionSnapshot Map(SnapshotInput input, IReadOnlyDictionary<Guid, string> maskedHandles)
    {
        var table = IncrementTable.Parse(input.Inc);
        var price = input.BidCount > 0 ? input.PriceC : input.StartC;

        var snapshot = new AuctionSnapshot
        {
            AuctionId = input.AuctionId.ToString(),
            Seq = input.Seq,
            Status = input.Status,
            Currency = input.Currency,
            CurrentPrice = Money(price, input.Currency),
            StartingPrice = Money(input.StartC, input.Currency),
            MinNextBid = Money(table.MinNext(input.PriceC, input.BidCount, input.StartC), input.Currency),
            BidCount = input.BidCount,
            Leader = input.Leader == null ? null : User(input.Leader.Value, maskedHandles),
            EndTimeMs = input.EndMs,
            ExtensionUsedSec = (int)(input.ExtUsedMs / 1000),
            // Only whether it is met; the amount never leaves (R1.5).
            ReserveMet = input.ReserveC == null ? null : input.BidCount > 0 && price >= input.ReserveC.Value,
            Watchers = input.Watchers,
            ServerTsMs = input.ServerTsMs,
        };

        foreach (var bid in input.Bids)
        {
            snapshot.RecentBids.Add(new PublicBidRow
            {
                Seq = bid.Seq,
                Bidder = User(bid.User, maskedHandles),
                Amount = Money(bid.AmountC, input.Currency),
                AtMs = bid.AtMs,
                Auto = bid.Kind is nameof(BidKind.ProxyAuto) or nameof(BidKind.AutoOutbid),
            });
        }

        foreach (var (fromC, incC) in table.Tiers)
        {
            snapshot.IncrementTiers.Add(new IncrementTier { From = Money(fromC, input.Currency), Increment = Money(incC, input.Currency) });
        }

        return snapshot;
    }

    /// <summary>Users whose handles the snapshot needs.</summary>
    public static IEnumerable<Guid> UsersIn(SnapshotInput input) =>
        input.Bids.Select(b => b.User).Concat(input.Leader == null ? [] : [input.Leader.Value]);

    private static PublicUser User(Guid id, IReadOnlyDictionary<Guid, string> handles) =>
        new() { UserId = id.ToString(), Handle = handles.GetValueOrDefault(id, "***") };

    private static Contracts.Money Money(long cents, string currency) => Domain.Money.FromCents(cents, currency).ToProto();
}

/// <summary>
/// Reads an auction's live picture (spec 04 design 2.3, task 3): from Redis in one transaction, so the
/// seq and the rows always match; from SQL when Redis does not hold the auction. Handles are masked.
/// </summary>
public sealed class SnapshotBuilder(
    IConnectionMultiplexer redis,
    RedisOptions options,
    AuctionDbContext db,
    UserHandleCache handles,
    TimeProvider clock,
    ILogger<SnapshotBuilder> log)
{
    private static readonly RedisValue[] Fields =
    [
        "status", "seq", "priceC", "startC", "bidCount", "leader", "endMs", "extUsedMs", "reserveC", "inc", "cur", "watchers",
    ];

    public async Task<AuctionSnapshot> BuildAsync(Guid auctionId, CancellationToken ct = default)
    {
        var input = await FromRedisAsync(auctionId) ?? await FromSqlAsync(auctionId, ct);
        var masked = await handles.GetMaskedAsync(SnapshotMapper.UsersIn(input), ct);
        return SnapshotMapper.Map(input, masked);
    }

    /// <summary>Null when Redis does not hold the auction (or is down).</summary>
    public async Task<SnapshotInput?> FromRedisAsync(Guid auctionId)
    {
        var keys = RedisKeys.For(auctionId, options);
        try
        {
            // MULTI/EXEC: no bid can land between reading the state and reading the rows.
            var transaction = redis.GetDatabase().CreateTransaction();
            var stateTask = transaction.HashGetAsync(keys.State, Fields);
            var bidsTask = transaction.SortedSetRangeByRankAsync(keys.Bids, 0, LiveStateReader.RecentBids - 1, Order.Descending);
            if (!await transaction.ExecuteAsync())
            {
                return null;
            }

            var v = await stateTask;
            if (v[0].IsNullOrEmpty || v[9].IsNullOrEmpty)
            {
                return null;
            }

            var serverMs = clock.GetUtcNow().ToUnixTimeMilliseconds();

            var bids = new List<PublicBidJson>();
            foreach (var member in await bidsTask)
            {
                try
                {
                    var row = JsonSerializer.Deserialize<PublicBidJson>(member.ToString());
                    if (row != null)
                    {
                        bids.Add(row);
                    }
                }
                catch (JsonException)
                {
                    // skip a bad row rather than fail the snapshot
                }
            }

            return new SnapshotInput(
                auctionId,
                Status: v[0].ToString(),
                Seq: Long(v[1]),
                PriceC: Long(v[2]),
                StartC: Long(v[3]),
                BidCount: (int)Long(v[4]),
                Leader: Guid.TryParse(v[5].ToString(), out var leader) ? leader : null,
                EndMs: Long(v[6]),
                ExtUsedMs: Long(v[7]),
                ReserveC: v[8].IsNullOrEmpty ? null : Long(v[8]),
                Inc: v[9].ToString(),
                Currency: v[10].IsNullOrEmpty ? "USD" : v[10].ToString(),
                Watchers: (int)Long(v[11]),
                Bids: bids,
                ServerTsMs: serverMs);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.LogWarning(ex, "Redis unavailable for the snapshot of {AuctionId}; using SQL", auctionId);
            return null;
        }
    }

    public async Task<SnapshotInput> FromSqlAsync(Guid auctionId, CancellationToken ct = default)
    {
        var auction = await db.Auctions.AsNoTracking().FirstOrDefaultAsync(a => a.AuctionId == auctionId, ct);
        if (auction == null || auction.Status is AuctionStatus.Draft or AuctionStatus.Cancelled)
        {
            throw new DomainException(ErrorCodes.NotFound, "Auction not found.");
        }

        var tiers = await db.BidIncrementTiers.AsNoTracking()
            .Where(t => t.TableId == auction.IncrementTableId)
            .Select(t => new { t.FromPrice, t.Increment })
            .ToListAsync(ct);
        var inc = IncrementTable.FromMoney(tiers.Select(t => (t.FromPrice, t.Increment))).ToRedisString();

        var rows = await db.Bids.AsNoTracking()
            .Where(b => b.AuctionId == auctionId && b.VoidedAt == null && b.Kind != BidKind.MaxRaise)
            .OrderByDescending(b => b.ESeq).ThenByDescending(b => b.Idx)
            .Take(LiveStateReader.RecentBids)
            .Select(b => new { Seq = b.Seq ?? 0, b.UserId, b.Amount, b.AcceptedAt, b.Kind })
            .ToListAsync(ct);
        var watchers = await db.Watchlist.CountAsync(w => w.AuctionId == auctionId, ct);

        return new SnapshotInput(
            auctionId,
            Status: auction.Status.ToString(),
            Seq: auction.LastSeq,
            PriceC: IncrementTable.ToCents(auction.CurrentPrice ?? auction.StartingPrice),
            StartC: IncrementTable.ToCents(auction.StartingPrice),
            BidCount: auction.BidCount,
            Leader: auction.CurrentLeaderId,
            EndMs: Ms(auction.EndTime),
            ExtUsedMs: (long)Math.Max(0, (auction.EndTime - auction.ScheduledEndTime).TotalMilliseconds),
            ReserveC: auction.ReservePrice == null ? null : IncrementTable.ToCents(auction.ReservePrice.Value),
            Inc: inc,
            Currency: auction.Currency,
            Watchers: watchers,
            Bids: rows.Select(r => new PublicBidJson(r.Seq, r.UserId, IncrementTable.ToCents(r.Amount), Ms(r.AcceptedAt), r.Kind.ToString())).ToList(),
            ServerTsMs: clock.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private static long Long(RedisValue value) =>
        long.TryParse(value.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
