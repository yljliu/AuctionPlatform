using Auction.Domain.Bidding;
using Auction.Domain.Catalog;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Infrastructure.Catalog;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Auction.Infrastructure.Simulation;
using Auction.Integration.Tests.Fixtures;
using Auction.Simulator.Sim;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using StoredEvent = Auction.Infrastructure.Persistence.Entities.AuctionEvent;

namespace Auction.Integration.Tests;

/// <summary>
/// Spec 08 tasks 1 and 6 on a real SQL Server: bot users, demo auctions with photos, the run table, and the
/// verifier on real rows (PASS, then a deleted Bids row gives FAIL naming the auction).
/// </summary>
[Collection(SqlServerCollection.Name)]
public class SimulationTests(SqlServerFixture sql)
{
    private static string TempImages() => Path.Combine(Path.GetTempPath(), "sim-images-" + Guid.NewGuid().ToString("N"));

    private static SimulationAdmin Admin(LifecycleTestDb t, string images) =>
        new(t.Db, TestServices.Auth(t.Db), new ImageStoreOptions { RootPath = images }, TimeProvider.System);

    private static async Task AddSellerAndCategoriesAsync(LifecycleTestDb t)
    {
        var now = DateTime.UtcNow;
        t.Db.Users.Add(new User { UserId = LifecycleTestDb.Seller, Username = "seller1", Email = "seller1@t.local", PasswordHash = "x", Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now });
        var parent = new Category { Name = "Watches", Slug = "watches" };
        t.Db.Categories.Add(parent);
        await t.Db.SaveChangesAsync();
        t.Db.Categories.AddRange(
            new Category { Name = "Vintage", Slug = "watches-vintage", ParentCategoryId = parent.CategoryId },
            new Category { Name = "Prints", Slug = "art-prints", ParentCategoryId = parent.CategoryId });
        await t.Db.SaveChangesAsync();
    }

    // ---------- task 1: bots ----------

    [Fact]
    public async Task Bots_are_made_once_marked_as_bots_and_get_working_tokens()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sim_bots");
        var admin = Admin(t, TempImages());

        var first = await admin.EnsureBotsAsync(3, "10.0.0.1");
        var again = await admin.EnsureBotsAsync(5, "10.0.0.1");

        first.Select(b => b.User.Username).Should().BeEquivalentTo("bot_0001", "bot_0002", "bot_0003");
        again.Should().HaveCount(5);
        await using var db = t.NewContext();
        var bots = await db.Users.Include(u => u.Roles).Where(u => u.Username.StartsWith("bot_")).ToListAsync();
        bots.Should().HaveCount(5, "the first three were reused");
        bots.Should().OnlyContain(u => u.IsBot && u.Status == UserStatus.Active && u.Roles.Count == 1 && u.Roles[0].Role == Role.Bidder);
        first[0].User.UserId.Should().Be(again.Single(b => b.User.Username == "bot_0001").User.UserId);

        var refreshed = await TestServices.Auth(t.NewContext()).RefreshAsync(first[0].Tokens.RefreshToken, "test", "10.0.0.1");
        refreshed.User.Username.Should().Be("bot_0001", "the tokens are real (R1.2)");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Bot_counts_are_limited(int count)
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sim_botlimit_" + count);

        var make = () => Admin(t, TempImages()).EnsureBotsAsync(count, null);

        (await make.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_person_with_a_bot_name_is_never_signed_in_as_a_bot()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sim_botname");
        var now = DateTime.UtcNow;
        t.Db.Users.Add(new User { UserId = Guid.NewGuid(), Username = "bot_0001", Email = "real@person.com", PasswordHash = "x", Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now });
        await t.Db.SaveChangesAsync();

        var make = () => Admin(t, TempImages()).EnsureBotsAsync(2, null);

        (await make.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Bot_token_issue_only_works_for_bots()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sim_botonly");
        var now = DateTime.UtcNow;
        var person = new User { UserId = Guid.NewGuid(), Username = "alice", Email = "a@t.local", PasswordHash = "x", Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now };
        t.Db.Users.Add(person);
        await t.Db.SaveChangesAsync();

        var issued = await TestServices.Auth(t.Db).IssueForBotsAsync([person.UserId], null);

        issued.Should().BeEmpty("a person's account is never opened without a password");
    }

    // ---------- task 1 and 4: demo auctions ----------

    [Fact]
    public async Task Demo_auctions_end_together_start_scheduled_and_have_a_photo_on_disk()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sim_demo");
        await AddSellerAndCategoriesAsync(t);
        var images = TempImages();
        try
        {
            var ids = await Admin(t, images).CreateDemoAuctionsAsync(new DemoAuctionSpec(4, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), "seller1", 30, 15, 300, "Spike"));

            ids.Should().HaveCount(4);
            await using var db = t.NewContext();
            var auctions = await db.Auctions.Where(a => ids.Contains(a.AuctionId)).ToListAsync();
            auctions.Should().OnlyContain(a => a.Status == AuctionStatus.Scheduled && a.SellerId == LifecycleTestDb.Seller);
            auctions.Select(a => a.EndTime).Distinct().Should().ContainSingle("they end together");
            (auctions[0].EndTime - auctions[0].StartTime).Should().Be(TimeSpan.FromMinutes(5));
            auctions.Should().OnlyContain(a => a.SnipeWindowSec == 30 && a.ExtensionSec == 15 && a.MaxTotalExtensionSec == 300);

            var itemIds = auctions.Select(a => a.ItemId).ToList();
            var pictures = await db.ItemImages.Where(i => itemIds.Contains(i.ItemId)).ToListAsync();
            pictures.Should().HaveCount(4).And.OnlyContain(i => i.Status == ImageStatus.Ready && i.ContentType == "image/jpeg");
            foreach (var picture in pictures)
            {
                var path = Path.Combine(images, picture.StorageKey.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(path).Should().BeTrue();
                File.Exists(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_thumb.jpg")).Should().BeTrue();
                ImageSniffer.Detect(await File.ReadAllBytesAsync(path)).Should().Be(ImageSniffer.Jpeg);
            }

            (await db.Items.Where(i => itemIds.Contains(i.ItemId)).Select(i => i.Title).ToListAsync()).Should().OnlyContain(title => title.StartsWith("Spike "));
        }
        finally
        {
            if (Directory.Exists(images))
            {
                Directory.Delete(images, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Demo_auction_limits_and_unknown_seller()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sim_demolimit");
        await AddSellerAndCategoriesAsync(t);
        var admin = Admin(t, TempImages());

        var tooMany = () => admin.CreateDemoAuctionsAsync(new DemoAuctionSpec(101, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), "seller1", 60, 60, 600, "x"));
        var tooLate = () => admin.CreateDemoAuctionsAsync(new DemoAuctionSpec(1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), "seller1", 60, 60, 600, "x"));
        var noSeller = () => admin.CreateDemoAuctionsAsync(new DemoAuctionSpec(1, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), "nobody", 60, 60, 600, "x"));

        (await tooMany.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
        (await tooLate.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
        (await noSeller.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.NotFound);
    }

    // ---------- task 2: run table ----------

    [Fact]
    public async Task Runs_are_saved_when_they_start_and_end_and_listed_newest_first()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sim_runs");
        var store = new SqlRunStore(t.Db.Database.GetConnectionString()!, TimeProvider.System);
        var admin = Guid.NewGuid();
        var first = new Run(new RunConfig("steady", 2, 1, [], 0, 0, 5, 2, 1), admin, "tok", new NoApi(), store, TimeProvider.System, NullLogger.Instance);
        await store.SaveStartAsync(first, default);
        await Task.Delay(50);
        var second = new Run(new RunConfig("closing-spike", 2, 1, [], 1, 1, 0, 2, 1), admin, "tok", new NoApi(), store, TimeProvider.System, NullLogger.Instance);
        await store.SaveStartAsync(second, default);

        await store.SaveEndAsync(second, default);   // ended with no report: shown as failed

        var list = await store.ListAsync(10, default);
        list.Select(r => r.RunId).Should().Equal(second.RunId.ToString(), first.RunId.ToString());
        list[0].Status.Should().Be("Failed");
        list[1].Status.Should().Be("Interrupted", "it never ended (the simulator was restarted)");
        await using var db = t.NewContext();
        (await db.SimulationRuns.CountAsync()).Should().Be(2);
        (await db.SimulationRuns.SingleAsync(r => r.RunId == first.RunId)).StartedBy.Should().Be(admin);
    }

    // ---------- task 6: verifier on real rows ----------

    private async Task<(Guid AuctionId, List<LedgerEntry> Ledger)> AuctionWithBidsAsync(LifecycleTestDb t)
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var a = await t.AddAuctionAsync(new(AuctionStatus.Settled, StartingPrice: 10m, CurrentPrice: 16m, Leader: alice, BidCount: 2));
        var k1 = Guid.NewGuid();
        var k2 = Guid.NewGuid();
        var at = DateTime.UtcNow;
        t.Db.Bids.AddRange(
            new Bid { AuctionId = a.AuctionId, UserId = alice, ESeq = 1, Idx = 0, Seq = 1, Kind = BidKind.Manual, Amount = 10m, MaxAmount = 20m, IdempotencyKey = k1, AcceptedAt = at },
            new Bid { AuctionId = a.AuctionId, UserId = bob, ESeq = 2, Idx = 0, Seq = 2, Kind = BidKind.AutoOutbid, Amount = 15m, MaxAmount = 15m, IdempotencyKey = k2, AcceptedAt = at },
            new Bid { AuctionId = a.AuctionId, UserId = alice, ESeq = 2, Idx = 1, Seq = 2, Kind = BidKind.ProxyAuto, Amount = 16m, AcceptedAt = at });
        t.Db.AuctionEvents.AddRange(
            new StoredEvent { AuctionId = a.AuctionId, Seq = 1, ESeq = 1, EventType = 1, Payload = "{\"priceC\":1000}", OccurredAt = at },
            new StoredEvent { AuctionId = a.AuctionId, Seq = 2, ESeq = 2, EventType = 1, Payload = "{\"priceC\":1600}", OccurredAt = at });
        await t.Db.SaveChangesAsync();
        return (a.AuctionId, [new LedgerEntry(a.AuctionId, k1, 1), new LedgerEntry(a.AuctionId, k2, 2)]);
    }

    [Fact]
    public async Task The_verifier_passes_real_rows_and_a_deleted_bids_row_fails_it_naming_the_auction()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sim_verify");
        var (id, ledger) = await AuctionWithBidsAsync(t);
        var store = new SqlRunStore(t.Db.Database.GetConnectionString()!, TimeProvider.System);
        var seen = new Dictionary<Guid, long> { [id] = 2 };

        var good = await store.LoadForVerifyAsync([id], ledger.Select(l => l.IdempotencyKey).ToList(), TimeSpan.Zero, default);
        var pass = VerifierChecks.Check(good, ledger, seen, bidsSent: 3);

        pass.Result.Should().Be("PASS", string.Join("; ", pass.Notes));
        good.Single().Increments.IncrementFor(1000).Should().BeGreaterThan(0);

        // Inject the fault: lose one accepted bid from SQL (R4.1).
        await t.Db.Bids.Where(b => b.IdempotencyKey == ledger[1].IdempotencyKey).ExecuteDeleteAsync();
        var broken = await store.LoadForVerifyAsync([id], ledger.Select(l => l.IdempotencyKey).ToList(), TimeSpan.Zero, default);
        var fail = VerifierChecks.Check(broken, ledger, seen, bidsSent: 3);

        fail.Result.Should().Be("FAIL");
        fail.MissingInSql.Should().Be(1);
        fail.FailingAuctions.Should().Equal(id.ToString());
    }

    [Fact]
    public async Task The_verifier_waits_for_ended_auctions_to_settle_and_gives_up_after_the_limit()
    {
        await using var t = await LifecycleTestDb.CreateAsync(sql, "sim_wait");
        var a = await t.AddAuctionAsync(new(AuctionStatus.Sealed, EndsInMs: -1000));
        var store = new SqlRunStore(t.Db.Database.GetConnectionString()!, TimeProvider.System);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var data = await store.LoadForVerifyAsync([a.AuctionId], [], TimeSpan.FromSeconds(3), default);

        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(2.5), "it kept waiting for the Sealed auction");
        data.Single().Status.Should().Be(AuctionStatus.Sealed);
        VerifierChecks.Check(data, [], new Dictionary<Guid, long>(), 0).StuckSealed.Should().Be(1, "R4.6");
    }

    private sealed class NoApi : IApiPort
    {
        public Task<IReadOnlyList<Contracts.BotSession>> CreateBotsAsync(string adminToken, int count, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> CreateDemoAuctionsAsync(string adminToken, Contracts.CreateDemoAuctionsRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListLiveAuctionsAsync(int max, CancellationToken ct) => throw new NotSupportedException();

        public Task<Contracts.PlaceBidResponse> PlaceBidAsync(string accessToken, Contracts.PlaceBidRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<Contracts.AuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<Contracts.AuctionEvent> SubscribeAsync(Guid auctionId, long lastSeenSeq, CancellationToken ct) => throw new NotSupportedException();
    }
}
