using System.Threading.Channels;
using Auction.Contracts;
using Auction.Contracts.Errors;
using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Simulator.Sim;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DomainMoney = Auction.Domain.Money;

namespace Auction.Domain.Tests;

/// <summary>
/// Spec 08 tasks 2, 3 and 6: whole runs against a fake Api that keeps real proxy-bidding state (ProxyResolver),
/// so the bots, scheduler, watchers, ledger and verifier all run. Nothing else is faked.
/// </summary>
public class SimulatorRunTests
{
    private sealed class FakeAuction
    {
        public Guid Id { get; } = Guid.NewGuid();
        public BidState State { get; set; } = new(0, null, 0, 0, 1000);
        public long Seq { get; set; }
        public long ESeq { get; set; }
        public List<BidRowData> Rows { get; } = [];
        public List<EventData> Events { get; } = [];
        public Channel<AuctionEvent> Live { get; } = Channel.CreateUnbounded<AuctionEvent>();
    }

    private sealed class FakeWorld : IApiPort, IRunStore
    {
        private static readonly IncrementTable Table = IncrementTable.Parse("0:100,5000:250");
        private readonly object _gate = new();
        public List<FakeAuction> Auctions { get; } = [];
        public List<string> Saved { get; } = [];
        public bool DropOneBid { get; set; }
        public TimeSpan PlaceBidDelay { get; set; }

        public FakeWorld(int auctions)
        {
            Auctions.AddRange(Enumerable.Range(0, auctions).Select(_ => new FakeAuction()));
        }

        // ----- IApiPort -----
        public Task<IReadOnlyList<BotSession>> CreateBotsAsync(string adminToken, int count, CancellationToken ct)
        {
            IReadOnlyList<BotSession> bots = Enumerable.Range(1, count).Select(i =>
            {
                var id = Guid.Parse($"b0700000-0000-0000-0000-{i:D12}");
                return new BotSession
                {
                    UserId = id.ToString(), Username = $"bot_{i:D4}",
                    Tokens = new AuthTokens { AccessToken = id.ToString(), AccessExpiresMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), RefreshToken = "r" },
                };
            }).ToList();
            return Task.FromResult(bots);
        }

        public Task<IReadOnlyList<Guid>> CreateDemoAuctionsAsync(string adminToken, CreateDemoAuctionsRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>(Auctions.Select(a => a.Id).ToList());

        public Task<IReadOnlyList<Guid>> ListLiveAuctionsAsync(int max, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>(Auctions.Select(a => a.Id).ToList());

        public Task<AuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) => throw new InvalidOperationException("not needed");

        public async Task<PlaceBidResponse> PlaceBidAsync(string accessToken, PlaceBidRequest request, CancellationToken ct)
        {
            if (PlaceBidDelay > TimeSpan.Zero)
            {
                await Task.Delay(PlaceBidDelay, ct);
            }

            var user = Guid.Parse(accessToken);
            var auction = Auctions.Single(a => a.Id.ToString() == request.AuctionId);
            var maxC = request.MaxAmount.ToDomain().ToCents();
            lock (_gate)
            {
                Resolution result;
                try
                {
                    result = ProxyResolver.Resolve(auction.State, user, maxC, Table);
                }
                catch (DomainException ex)
                {
                    throw RpcErrors.From(ex, "corr-fake");
                }

                auction.State = result.Next;
                auction.ESeq++;
                var publicChange = result.Result != BidResult.MaxRaised;
                if (publicChange)
                {
                    auction.Seq++;
                }

                var key = Guid.Parse(request.IdempotencyKey);
                auction.Rows.Add(new BidRowData(auction.ESeq, 0, BidKind.Manual, user, result.Rows[0].AmountC / 100m, maxC / 100m, key, false));
                for (var i = 1; i < result.Rows.Count; i++)
                {
                    auction.Rows.Add(new BidRowData(auction.ESeq, (byte)i, result.Rows[i].Kind, result.Rows[i].User, result.Rows[i].AmountC / 100m, null, null, false));
                }

                if (publicChange)
                {
                    auction.Events.Add(new EventData(auction.Seq, "Bid", $"{{\"priceC\":{result.Next.PriceC}}}"));
                    auction.Live.Writer.TryWrite(new AuctionEvent
                    {
                        Seq = auction.Seq, AuctionId = request.AuctionId,
                        BidAccepted = new BidAccepted
                        {
                            Price = DomainMoney.FromCents(result.Next.PriceC, "USD").ToProto(),
                            MinNextBid = DomainMoney.FromCents(Table.MinNext(result.Next.PriceC, result.Next.BidCount, 1000), "USD").ToProto(),
                            BidCount = result.Next.BidCount, Leader = new PublicUser { UserId = result.Next.Leader.ToString() },
                            EndTimeMs = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
                        },
                    });
                }

                return new PlaceBidResponse
                {
                    Result = PlaceBidResponse.Types.Result.Leading, Seq = auction.Seq,
                    CurrentPrice = DomainMoney.FromCents(result.Next.PriceC, "USD").ToProto(),
                };
            }
        }

        public async IAsyncEnumerable<AuctionEvent> SubscribeAsync(Guid auctionId, long lastSeenSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var auction = Auctions.Single(a => a.Id == auctionId);
            var snapshot = new AuctionSnapshot
            {
                AuctionId = auctionId.ToString(), Seq = auction.Seq, Status = "Live", Currency = "USD",
                CurrentPrice = DomainMoney.FromCents(auction.State.PriceC, "USD").ToProto(), StartingPrice = DomainMoney.FromCents(1000, "USD").ToProto(),
                MinNextBid = DomainMoney.FromCents(Table.MinNext(auction.State.PriceC, auction.State.BidCount, 1000), "USD").ToProto(),
                BidCount = auction.State.BidCount, EndTimeMs = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
            };
            snapshot.IncrementTiers.Add(new IncrementTier { From = DomainMoney.FromCents(0, "USD").ToProto(), Increment = DomainMoney.FromCents(100, "USD").ToProto() });
            snapshot.IncrementTiers.Add(new IncrementTier { From = DomainMoney.FromCents(5000, "USD").ToProto(), Increment = DomainMoney.FromCents(250, "USD").ToProto() });
            yield return new AuctionEvent { Seq = auction.Seq, AuctionId = auctionId.ToString(), Snapshot = snapshot };

            await foreach (var e in auction.Live.Reader.ReadAllAsync(ct))
            {
                yield return e;
            }
        }

        // ----- IRunStore -----
        public Task SaveStartAsync(Run run, CancellationToken ct)
        {
            Saved.Add("start:" + run.RunId);
            return Task.CompletedTask;
        }

        public Task SaveEndAsync(Run run, CancellationToken ct)
        {
            Saved.Add("end:" + run.Status);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunInfo>> ListAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<RunInfo>>([]);

        public Task<IReadOnlyList<AuctionData>> LoadForVerifyAsync(IReadOnlyList<Guid> ids, IReadOnlyCollection<Guid> keys, TimeSpan wait, CancellationToken ct)
        {
            lock (_gate)
            {
                var first = true;
                IReadOnlyList<AuctionData> data = Auctions.Where(a => ids.Contains(a.Id)).Select(a =>
                {
                    var rows = a.Rows.ToList();
                    if (DropOneBid && first && rows.Count > 0)
                    {
                        var dropped = rows.First(x => x.IdempotencyKey != null).IdempotencyKey;
                        rows.RemoveAll(r => r.IdempotencyKey == dropped);
                        first = false;
                    }

                    var end = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
                    return new AuctionData(a.Id, AuctionStatus.Settled, a.State.BidCount == 0 ? null : a.State.PriceC / 100m, a.State.Leader,
                        end, end, 600, 10m, Table, rows, a.Events.ToList());
                }).ToList();
                return Task.FromResult(data);
            }
        }
    }

    private static RunConfig Steady(int bots = 4, double rate = 20, int duration = 2) =>
        new(Scenarios.Steady, bots, rate, [], 0, 0, duration, 3, Seed: 11);

    private static Run NewRun(FakeWorld world, RunConfig config) =>
        new(config, Guid.NewGuid(), "admin-token", world, world, TimeProvider.System, NullLogger.Instance);

    [Fact]
    public async Task A_steady_run_on_two_auctions_passes_the_verifier()
    {
        var world = new FakeWorld(2);
        var run = NewRun(world, Steady());

        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        run.Status.Should().Be(RunStatus.Passed, run.Message);
        run.Report!.Passed.Should().BeTrue();
        run.Report.Auctions.Should().Be(2);
        run.Report.Accepted.Should().BeGreaterThan(0);
        run.Report.Accepted.Should().Be(world.Auctions.Sum(a => a.Rows.Count(r => r.IdempotencyKey != null)));
        world.Saved.Should().Contain(s => s.StartsWith("start:")).And.Contain("end:Passed");
        run.Info().ReportJson.Should().Contain("\"result\": \"PASS\"");
    }

    [Fact]
    public async Task Counters_and_the_latency_numbers_move()
    {
        var world = new FakeWorld(1) { PlaceBidDelay = TimeSpan.FromMilliseconds(20) };
        var run = NewRun(world, Steady(rate: 30, duration: 2));

        run.Start();
        await Task.Delay(1500);
        var m = run.Metrics();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        m.BidsSent.Should().BeGreaterThan(5);
        m.Accepted.Should().BeGreaterThan(0);
        m.ActiveBots.Should().Be(4);
        m.P50Ms.Should().BeGreaterThanOrEqualTo(15);
        m.P99Ms.Should().BeGreaterThanOrEqualTo(m.P50Ms);
        m.RecentEvents.Should().NotBeEmpty();
        run.Metrics().Rejected.Should().Be(run.Metrics().RejectedByCode.Sum(c => c.Count));
    }

    [Fact]
    public async Task A_missing_bid_row_fails_the_run_and_names_the_auction()
    {
        var world = new FakeWorld(2) { DropOneBid = true };
        var run = NewRun(world, Steady());

        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        run.Status.Should().Be(RunStatus.Failed);
        run.Report!.MissingInSql.Should().BeGreaterThan(0);
        run.Report.FailingAuctions.Should().Equal(world.Auctions[0].Id.ToString());
    }

    [Fact]
    public async Task Stop_stops_the_bots_within_2_seconds_then_verifies()
    {
        var world = new FakeWorld(1);
        var run = NewRun(world, Steady(duration: 600));
        run.Start();
        await Task.Delay(800);
        var sentBefore = run.Metrics().BidsSent;
        sentBefore.Should().BeGreaterThan(0);

        var stopped = DateTime.UtcNow;
        run.Stop();
        await Wait(() => run.ActiveBots == 0, TimeSpan.FromSeconds(2));
        (DateTime.UtcNow - stopped).Should().BeLessThan(TimeSpan.FromSeconds(2));

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        run.Status.Should().Be(RunStatus.Passed, "the bids made before the stop still verify");
        run.Metrics().BidsSent.Should().BeGreaterThanOrEqualTo(sentBefore);
    }

    [Fact]
    public async Task A_second_start_while_one_runs_is_run_already_active()
    {
        var world = new FakeWorld(1);
        var manager = new RunManager(world, world, TimeProvider.System, NullLogger<RunManager>.Instance);
        var first = manager.Start(new RunConfig(Scenarios.Steady, 2, 5, [], 0, 0, 600, 2, 3), Guid.NewGuid(), "t");

        var second = () => manager.Start(new RunConfig(Scenarios.Steady, 2, 5, [], 0, 0, 600, 2, 3), Guid.NewGuid(), "t");

        second.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.RunAlreadyActive);
        manager.Find(null).Should().BeSameAs(first);

        first.Stop();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var third = () => manager.Start(new RunConfig(Scenarios.Steady, 2, 5, [], 0, 0, 600, 2, 3), Guid.NewGuid(), "t");
        third.Should().NotThrow("the first run is finished");
        manager.Active!.Stop();
        await manager.Find(null)!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Adjust_changes_the_bots_and_rate_within_2_seconds()
    {
        var world = new FakeWorld(1);
        var run = NewRun(world, Steady(bots: 2, rate: 5, duration: 600));
        run.Start();
        await Wait(() => run.Status == RunStatus.Running, TimeSpan.FromSeconds(5));

        await run.AdjustAsync(bots: 6, rate: 12, CancellationToken.None);

        run.ActiveBots.Should().Be(6);
        run.RatePerAuction.Should().Be(12);
        await run.AdjustAsync(bots: 3, rate: 0, CancellationToken.None);
        run.ActiveBots.Should().Be(3);
        run.RatePerAuction.Should().Be(12, "0 keeps the rate");

        run.Stop();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task No_auctions_to_bid_on_fails_with_a_clear_message()
    {
        var world = new FakeWorld(0);
        var run = NewRun(world, Steady());

        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        run.Status.Should().Be(RunStatus.Failed);
        run.Message.Should().Contain("No auctions to bid on");
    }

    private static async Task Wait(Func<bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met in time");
            }

            await Task.Delay(20);
        }
    }
}
