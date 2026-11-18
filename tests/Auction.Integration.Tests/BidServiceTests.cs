using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Redis;
using Auction.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 03 task 6: the whole PlaceBid flow with SQL and Redis.
/// Test Redis runs without a replica, so WAIT always answers 0.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class BidServiceTests(SqlServerFixture sql, RedisFixture redis) : IAsyncLifetime
{
    private AuctionDbContext _db = null!;
    private Guid _bidder, _banned, _suspended, _seller;

    public async Task InitializeAsync()
    {
        _db = await sql.CreateEmptyContextAsync("bid_service");
        await _db.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        User NewUser(string name, UserStatus status) => new()
        {
            UserId = Guid.NewGuid(), Username = name, Email = name + "@test.local", PasswordHash = "x",
            Status = status, CreatedAt = now, UpdatedAt = now,
        };

        var users = new[]
        {
            NewUser("bidder", UserStatus.Active), NewUser("banned", UserStatus.Banned),
            NewUser("suspended", UserStatus.Suspended), NewUser("seller", UserStatus.Active),
        };
        _db.Users.AddRange(users);
        await _db.SaveChangesAsync();
        (_bidder, _banned, _suspended, _seller) = (users[0].UserId, users[1].UserId, users[2].UserId, users[3].UserId);

        // The status cache must not hold old values from another run.
        foreach (var user in users)
        {
            await redis.Connection.GetDatabase().KeyDeleteAsync(RedisKeys.UserStatus(user.UserId));
        }
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private BidService Service(bool requireReplica = false, IRateLimiter? limiter = null) => new(
        new BidScript(new ScriptRegistry(redis.Connection)),
        limiter ?? new RedisRateLimiter(redis.Connection, NullLogger<RedisRateLimiter>.Instance),
        TestServices.StatusCache(_db, redis.Connection),
        _db,
        redis.Connection,
        new RedisOptions { RequireReplicaAck = requireReplica },
        NullLogger<BidService>.Instance);

    /// <summary>Lets every bid through, so tests that are not about the limit cannot hit it.</summary>
    private sealed class NoLimit : IRateLimiter
    {
        public Task<RateLimitResult> TryAcquireAsync(RateLimitPolicy policy, string id, int cost = 1) =>
            Task.FromResult(new RateLimitResult(true, 0));
    }

    private async Task<Guid> LiveAuctionAsync() =>
        (await LiveAuctionSeeder.SeedAsync(redis.Connection.GetDatabase(), new(Guid.NewGuid(), _seller))).AuctionId;

    private static PlaceBidCommand Bid(Guid user, Guid auction, long units, int nanos = 0, string currency = "USD", Guid? idem = null) =>
        new(user, auction.ToString(), units, nanos, currency, (idem ?? Guid.NewGuid()).ToString(), "test");

    private static async Task<DomainException> FailsWith(Func<Task> act, string code)
    {
        var ex = (await act.Should().ThrowAsync<DomainException>()).Which;
        ex.Code.Should().Be(code);
        return ex;
    }

    [Fact]
    public async Task Active_user_places_a_bid()
    {
        var auction = await LiveAuctionAsync();

        var outcome = await Service(limiter: new NoLimit()).PlaceAsync(Bid(_bidder, auction, 50));

        outcome.Result.Should().Be(BidResult.Leading);
        outcome.PriceC.Should().Be(1000);
        outcome.IsLeader.Should().BeTrue();
    }

    [Fact]
    public async Task Banned_user_gets_ACCOUNT_BANNED_and_the_script_never_runs()
    {
        var auction = await LiveAuctionAsync();

        await FailsWith(() => Service(limiter: new NoLimit()).PlaceAsync(Bid(_banned, auction, 50)), ErrorCodes.AccountBanned);

        var eseq = (long)await redis.Connection.GetDatabase().HashGetAsync(RedisKeys.For(auction, 1).State, "eseq");
        eseq.Should().Be(0);
    }

    [Fact]
    public async Task Suspended_user_gets_ACCOUNT_SUSPENDED()
    {
        var auction = await LiveAuctionAsync();

        await FailsWith(() => Service(limiter: new NoLimit()).PlaceAsync(Bid(_suspended, auction, 50)), ErrorCodes.AccountSuspended);
    }

    [Fact]
    public async Task Sixth_bid_in_one_second_is_RATE_LIMITED()
    {
        var auction = await LiveAuctionAsync();
        var service = Service();
        await redis.Connection.GetDatabase().KeyDeleteAsync(RedisKeys.RateLimit("bid", _bidder.ToString()));
        await TestServices.StatusCache(_db, redis.Connection).GetAsync(_bidder);   // warm the cache: no SQL during the burst

        // 10 bids at the same moment; the bucket holds 5.
        var codes = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            try
            {
                await service.PlaceAsync(Bid(_bidder, auction, 1000 + (i * 100)));
                return ("ok", 0L);
            }
            catch (DomainException ex)
            {
                return (ex.Code, ex.RetryAfterMs);
            }
        }));

        var limited = codes.Where(c => c.Item1 == ErrorCodes.RateLimited).ToList();
        limited.Should().HaveCountGreaterThanOrEqualTo(4, "5 tokens, plus at most one refilled while the burst ran");
        limited.Should().OnlyContain(c => c.Item2 > 0);
    }

    [Fact]
    public async Task Three_decimals_is_VALIDATION_FAILED()
    {
        var auction = await LiveAuctionAsync();

        var ex = await FailsWith(() => Service(limiter: new NoLimit()).PlaceAsync(Bid(_bidder, auction, 10, 1_000_000)), ErrorCodes.ValidationFailed);

        ex.Fields.Single().Rule.Should().Be("decimals");
    }

    [Fact]
    public async Task Wrong_currency_is_VALIDATION_FAILED()
    {
        var auction = await LiveAuctionAsync();

        await FailsWith(() => Service(limiter: new NoLimit()).PlaceAsync(Bid(_bidder, auction, 50, currency: "EUR")), ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Below_minimum_carries_the_min_next_bid()
    {
        var auction = await LiveAuctionAsync();

        var ex = await FailsWith(() => Service(limiter: new NoLimit()).PlaceAsync(Bid(_bidder, auction, 5)), ErrorCodes.BelowMinimum);

        ex.MinNextBidC.Should().Be(1000);
    }

    [Fact]
    public async Task Auction_not_in_redis_is_NOT_LIVE_if_it_exists_and_NOT_FOUND_if_not()
    {
        var category = new Category { Name = "c", Slug = "c" };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        var item = new Item
        {
            ItemId = Guid.NewGuid(), SellerId = _seller, CategoryId = category.CategoryId, Title = "t", Description = "d",
            ShippingType = ShippingType.FreeShipping, Status = ItemStatus.Published, CreatedAt = now, UpdatedAt = now,
        };
        var scheduled = new Infrastructure.Persistence.Entities.Auction
        {
            AuctionId = Guid.NewGuid(), ItemId = item.ItemId, SellerId = _seller, StartTime = now.AddHours(1), ScheduledEndTime = now.AddHours(2),
            EndTime = now.AddHours(2), StartingPrice = 10, FeeScheduleId = 1, Status = AuctionStatus.Scheduled, CreatedAt = now, UpdatedAt = now,
        };
        _db.Items.Add(item);
        _db.Auctions.Add(scheduled);
        await _db.SaveChangesAsync();

        await FailsWith(() => Service(limiter: new NoLimit()).PlaceAsync(Bid(_bidder, scheduled.AuctionId, 50)), ErrorCodes.NotLive);
        await FailsWith(() => Service(limiter: new NoLimit()).PlaceAsync(Bid(_bidder, Guid.NewGuid(), 50)), ErrorCodes.NotFound);
    }

    [Fact]
    public async Task No_replica_with_the_flag_on_is_SERVICE_UNAVAILABLE_and_the_retry_gets_the_stored_success()
    {
        var auction = await LiveAuctionAsync();
        var idem = Guid.NewGuid();

        // Replica required, none running: the bid is in Redis but not confirmed to the user.
        await FailsWith(() => Service(requireReplica: true, limiter: new NoLimit()).PlaceAsync(Bid(_bidder, auction, 50, idem: idem)),
            ErrorCodes.ServiceUnavailable);

        // "Replica back" (flag off here): the same key returns the first outcome, not a second bid.
        var retry = await Service(limiter: new NoLimit()).PlaceAsync(Bid(_bidder, auction, 50, idem: idem));

        retry.Accepted.Should().BeTrue();
        retry.Replayed.Should().BeTrue();
        retry.Seq.Should().Be(1);
        ((long)await redis.Connection.GetDatabase().HashGetAsync(RedisKeys.For(auction, 1).State, "eseq")).Should().Be(1);
    }

    [Fact]
    public async Task Bad_ids_are_VALIDATION_FAILED()
    {
        var bad = new PlaceBidCommand(_bidder, "not-a-guid", 50, 0, "USD", "also-bad", "t");

        var ex = await FailsWith(() => Service(limiter: new NoLimit()).PlaceAsync(bad), ErrorCodes.ValidationFailed);

        ex.Fields.Single().Field.Should().Be("auction_id");
    }
}
