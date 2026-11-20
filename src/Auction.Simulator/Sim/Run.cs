using System.Collections.Concurrent;
using System.Diagnostics;
using Auction.Contracts;
using Auction.Contracts.Errors;
using Auction.Domain.Bidding;
using Grpc.Core;

namespace Auction.Simulator.Sim;

public sealed record RunConfig(
    string Scenario,
    int Bots,
    double RatePerAuction,
    IReadOnlyList<Guid> Targets,
    int DemoAuctions,
    int DemoMinutes,
    int DurationSec,
    int Aggressiveness,
    long Seed);

public enum RunStatus
{
    Starting,
    Running,
    Verifying,
    Passed,
    Failed,
    Stopped,
}

/// <summary>Where runs are saved (ops.SimulationRuns) and where the verifier reads SQL. Fakes in tests.</summary>
public interface IRunStore
{
    Task SaveStartAsync(Run run, CancellationToken ct);

    Task SaveEndAsync(Run run, CancellationToken ct);

    Task<IReadOnlyList<RunInfo>> ListAsync(int limit, CancellationToken ct);

    /// <summary>Waits (up to the limit) until ended auctions settle and accepted bids are in SQL, then reads them.</summary>
    Task<IReadOnlyList<AuctionData>> LoadForVerifyAsync(IReadOnlyList<Guid> auctionIds, IReadOnlyCollection<Guid> acceptedKeys, TimeSpan wait, CancellationToken ct);
}

/// <summary>
/// One simulation run (spec 08 tasks 2 to 6): bots, a 100 ms scheduler with a token bucket per auction,
/// a watcher per auction, metrics, a ledger of accepted bids, then the verifier.
/// </summary>
public sealed class Run
{
    public const int MaxInFlight = 256;
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan StopWithin = TimeSpan.FromSeconds(2);

    private readonly IApiPort _api;
    private readonly IRunStore _store;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly string _adminToken;
    private readonly CancellationTokenSource _bidding = new();
    private readonly CancellationTokenSource _watching = new();
    private readonly SemaphoreSlim _inFlight = new(MaxInFlight, MaxInFlight);
    private readonly ConcurrentDictionary<string, long> _rejectedByCode = new();
    private readonly ConcurrentQueue<RunEvent> _events = new();
    private readonly ConcurrentBag<LedgerEntry> _ledger = [];
    private readonly LatencyReservoir _latency = new();
    private readonly List<Bot> _bots = [];
    private readonly List<AuctionWatch> _watches = [];
    private readonly Dictionary<Guid, TokenBucket> _buckets = [];
    private readonly Random _random;
    private long _sent;
    private long _accepted;
    private long _rejected;
    private long _sentAtLastSecond;
    private double _bidsPerSec;
    private int _activeBots;
    private double _rate;
    private Task? _main;

    public Run(RunConfig config, Guid startedBy, string adminToken, IApiPort api, IRunStore store, TimeProvider clock, ILogger log)
    {
        Config = config;
        StartedBy = startedBy;
        _adminToken = adminToken;
        _api = api;
        _store = store;
        _clock = clock;
        _log = log;
        _activeBots = config.Bots;
        _rate = config.RatePerAuction;
        _random = config.Seed == 0 ? new Random() : new Random(unchecked((int)config.Seed));
        StartedAt = clock.GetUtcNow();
    }

    public Guid RunId { get; } = Guid.NewGuid();

    public RunConfig Config { get; }

    public Guid StartedBy { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? EndedAt { get; private set; }

    public RunStatus Status { get; private set; } = RunStatus.Starting;

    public string Message { get; private set; } = "Starting";

    public VerifyReport? Report { get; private set; }

    public IReadOnlyList<Guid> Targets
    {
        get
        {
            lock (_watches)
            {
                return _watches.Select(w => w.AuctionId).ToList();
            }
        }
    }

    /// <summary>Number of this run's first bot (bot_0001 = 1). Runs side by side use different ranges.</summary>
    public int FirstBot { get; init; } = 1;

    /// <summary>Auctions other active runs bid on; a steady run leaves them alone so each verifier sees only its own bids.</summary>
    public Func<IReadOnlyCollection<Guid>>? BusyAuctions { get; init; }

    public bool IsActive => Status is RunStatus.Starting or RunStatus.Running or RunStatus.Verifying;

    public bool BotsStopped => _bidding.IsCancellationRequested;

    /// <summary>Tests and the Simulator host can wait for the whole run.</summary>
    public Task Completion => _main ?? Task.CompletedTask;

    public int ActiveBots => Volatile.Read(ref _activeBots);

    public double RatePerAuction => Volatile.Read(ref _rate);

    public void Start() => _main = Task.Run(RunAsync);

    /// <summary>Stops the bots (they stop within 2 s), then the verifier runs (R3.2).</summary>
    public void Stop()
    {
        if (!_bidding.IsCancellationRequested)
        {
            AddEvent(Guid.Empty, "Stop requested");
            _bidding.Cancel();
        }
    }

    /// <summary>Changes the bot count or the rate; the scheduler reads them every 100 ms (R3.3).</summary>
    public async Task AdjustAsync(int bots, double rate, CancellationToken ct)
    {
        if (rate > 0)
        {
            Volatile.Write(ref _rate, rate);
        }

        if (bots > 0)
        {
            if (bots > _bots.Count)
            {
                await AddBotsAsync(bots, ct);
            }

            Volatile.Write(ref _activeBots, Math.Min(bots, _bots.Count));
        }

        AddEvent(Guid.Empty, $"Adjusted: {ActiveBots} bots, {RatePerAuction:0.##} bids/s per auction");
    }

    public RunMetrics Metrics()
    {
        var m = new RunMetrics
        {
            RunId = RunId.ToString(), Status = Status.ToString(), AtMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            BidsSent = Interlocked.Read(ref _sent), Accepted = Interlocked.Read(ref _accepted), Rejected = Interlocked.Read(ref _rejected),
            P50Ms = _latency.Percentile(50), P99Ms = _latency.Percentile(99), ActiveBots = ActiveBots,
            Extensions = _watches.Sum(w => w.Extensions), BidsPerSec = Math.Round(_bidsPerSec, 1),
        };
        m.RejectedByCode.AddRange(_rejectedByCode.OrderByDescending(p => p.Value).Select(p => new CodeCount { Code = p.Key, Count = p.Value }));
        m.RecentEvents.AddRange(_events.Reverse().Take(50));
        return m;
    }

    public RunInfo Info()
    {
        var info = new RunInfo
        {
            RunId = RunId.ToString(), Scenario = Config.Scenario, Status = Status.ToString(), StartedAtMs = StartedAt.ToUnixTimeMilliseconds(),
            EndedAtMs = EndedAt?.ToUnixTimeMilliseconds() ?? 0, Bots = ActiveBots, RatePerAuction = RatePerAuction,
            ReportJson = Report?.ToJson() ?? "", Message = Message,
        };
        info.TargetAuctionIds.AddRange(Targets.Select(t => t.ToString()));
        return info;
    }

    private async Task RunAsync()
    {
        try
        {
            await _store.SaveStartAsync(this, CancellationToken.None);
            await SetUpAsync(_bidding.Token);
            Status = RunStatus.Running;
            Message = "Bidding";
            await ScheduleAsync(_bidding.Token);
        }
        catch (OperationCanceledException) when (_bidding.IsCancellationRequested)
        {
            // stopped during set-up or bidding
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Simulation run {RunId} failed", RunId);
            Message = "Failed: " + (ex is RpcException rpc ? RpcErrors.MessageOf(rpc) : ex.Message);
            _bidding.Cancel();
            await FinishAsync(RunStatus.Failed);
            return;
        }

        _bidding.Cancel();
        await WaitForInFlightAsync();
        await VerifyAsync();
    }

    private async Task SetUpAsync(CancellationToken ct)
    {
        Message = $"Signing in {Config.Bots} bots";
        await AddBotsAsync(Config.Bots, ct);

        IReadOnlyList<Guid> targets;
        if (Scenarios.MakesAuctions(Config.Scenario))
        {
            var snipe = Config.Scenario == Scenarios.SnipeWar;
            Message = $"Creating {Config.DemoAuctions} auctions";
            targets = await _api.CreateDemoAuctionsAsync(_adminToken, new CreateDemoAuctionsRequest
            {
                Count = Config.DemoAuctions, StartsInSec = 15, DurationSec = Math.Max(1, Config.DemoMinutes) * 60,
                SnipeWindowSec = snipe ? 30 : 60, ExtensionSec = snipe ? 15 : 60, MaxTotalExtensionSec = snipe ? 300 : 600,
                TitlePrefix = snipe ? "Snipe war lot" : "Closing spike lot",
            }, ct);
        }
        else
        {
            targets = Config.Targets.Count > 0 ? Config.Targets : await _api.ListLiveAuctionsAsync(50, ct);
            var busy = BusyAuctions?.Invoke() ?? [];
            targets = targets.Where(id => !busy.Contains(id)).ToList();
        }

        if (targets.Count == 0)
        {
            throw new InvalidOperationException("No auctions to bid on. Pick targets, or make Live auctions first.");
        }

        foreach (var id in targets.Distinct())
        {
            var watch = new AuctionWatch(id, AddEvent);
            lock (_watches)
            {
                _watches.Add(watch);
            }

            _buckets[id] = new TokenBucket();
            _ = watch.RunAsync(_api, _watching.Token);
        }

        AddEvent(Guid.Empty, $"Started {Config.Scenario}: {_bots.Count} bots on {_watches.Count} auctions");
    }

    private async Task AddBotsAsync(int count, CancellationToken ct)
    {
        var sessions = await _api.CreateBotsAsync(_adminToken, FirstBot, count, ct);
        lock (_bots)
        {
            foreach (var s in sessions.Where(s => _bots.All(b => b.UserId.ToString() != s.UserId)))
            {
                _bots.Add(new Bot(Guid.Parse(s.UserId), s.Username, s.Tokens));
            }
        }
    }

    private async Task ScheduleAsync(CancellationToken ct)
    {
        var steadyEnd = Config.Scenario == Scenarios.Steady ? StartedAt + TimeSpan.FromSeconds(Math.Max(1, Config.DurationSec)) : DateTimeOffset.MaxValue;
        var giveUp = StartedAt + TimeSpan.FromMinutes(Math.Max(1, Config.DemoMinutes) + 20);   // ends plus extensions, then stop anyway
        var last = _clock.GetUtcNow();
        var lastSecond = last;
        using var timer = new PeriodicTimer(Tick, _clock);

        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = _clock.GetUtcNow();
            var elapsed = now - last;
            last = now;

            if (now - lastSecond >= TimeSpan.FromSeconds(1))
            {
                var sent = Interlocked.Read(ref _sent);
                _bidsPerSec = (sent - _sentAtLastSecond) / (now - lastSecond).TotalSeconds;
                _sentAtLastSecond = sent;
                lastSecond = now;
            }

            // The duration ends the run by itself (R3.4): steady by time, the others when every auction has closed.
            if (now >= steadyEnd || (Scenarios.MakesAuctions(Config.Scenario) && (_watches.All(w => w.IsOver) || now >= giveUp)))
            {
                AddEvent(Guid.Empty, "Duration reached");
                return;
            }

            var nowMs = now.ToUnixTimeMilliseconds();
            foreach (var watch in _watches)
            {
                var view = watch.Read();
                if (view.Status != "Live" || view.Increments == null)
                {
                    continue;
                }

                var rate = Scenarios.RateFor(Config.Scenario, RatePerAuction, nowMs, view.EndMs, Config.Scenario == Scenarios.SnipeWar ? 30 : 60);
                var count = _buckets[watch.AuctionId].Take(rate, elapsed);
                for (var i = 0; i < count; i++)
                {
                    TryBid(watch, view.PriceC, view.MinNextC, view.StartC, view.Leader, view.Increments, nowMs, ct);
                }
            }
        }
    }

    private void TryBid(AuctionWatch watch, long priceC, long minNextC, long startC, Guid? leader, IncrementTable increments, long nowMs, CancellationToken ct)
    {
        var active = ActiveBots;
        if (active == 0 || _bots.Count == 0)
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Bot bot;
            long? max;
            lock (_random)
            {
                bot = _bots[_random.Next(Math.Min(active, _bots.Count))];
                max = BidChooser.Choose(minNextC, priceC, increments, Config.Aggressiveness, bot.Ceiling(watch.AuctionId, startC, _random),
                    bot.UserId == leader, _random);
            }

            if (max == null)
            {
                continue;
            }

            if (!_inFlight.Wait(0))
            {
                return;   // too many bids already on their way; skip this one
            }

            _ = SendAsync(bot, watch.AuctionId, max.Value, nowMs, ct);
            return;
        }
    }

    private async Task SendAsync(Bot bot, Guid auctionId, long maxC, long nowMs, CancellationToken ct)
    {
        var key = Guid.NewGuid();
        var watch = Stopwatch.StartNew();
        try
        {
            await bot.EnsureFreshAsync(_api, nowMs, ct);
            Interlocked.Increment(ref _sent);
            var reply = await _api.PlaceBidAsync(bot.AccessToken, new PlaceBidRequest
            {
                AuctionId = auctionId.ToString(), MaxAmount = Domain.Money.FromCents(maxC, "USD").ToProto(), IdempotencyKey = key.ToString(),
            }, ct);
            _latency.Add(watch.Elapsed.TotalMilliseconds);
            Interlocked.Increment(ref _accepted);
            _ledger.Add(new LedgerEntry(auctionId, key, reply.Seq));
        }
        catch (RpcException ex)
        {
            _latency.Add(watch.Elapsed.TotalMilliseconds);
            Interlocked.Increment(ref _rejected);
            var code = RpcErrors.CodeOf(ex);
            _rejectedByCode.AddOrUpdate(code, 1, (_, n) => n + 1);
            if (code == "UNAUTHENTICATED")
            {
                try
                {
                    await bot.EnsureFreshAsync(_api, nowMs, CancellationToken.None, force: true);
                }
                catch (RpcException)
                {
                    // try again on its next bid
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        finally
        {
            _inFlight.Release();
        }
    }

    private async Task WaitForInFlightAsync()
    {
        var deadline = _clock.GetUtcNow() + StopWithin;
        while (_inFlight.CurrentCount < MaxInFlight && _clock.GetUtcNow() < deadline)
        {
            await Task.Delay(50);
        }

        Volatile.Write(ref _activeBots, 0);
    }

    private async Task VerifyAsync()
    {
        Status = RunStatus.Verifying;
        Message = "Verifying: waiting for auctions to settle and bids to reach SQL";
        AddEvent(Guid.Empty, "Bots stopped; verifying");
        try
        {
            var ledger = _ledger.ToList();
            var data = await _store.LoadForVerifyAsync(Targets, ledger.Select(l => l.IdempotencyKey).ToHashSet(), TimeSpan.FromMinutes(2), CancellationToken.None);
            await Task.Delay(1500);   // let the watchers receive the last events
            var seen = _watches.ToDictionary(w => w.AuctionId, w => w.Seq);
            Report = VerifierChecks.Check(data, ledger, seen, Interlocked.Read(ref _sent));
            Message = Report.Passed ? "Verifier passed" : $"Verifier failed on {Report.FailingAuctions.Count} auctions";
            await FinishAsync(Report.Passed ? RunStatus.Passed : RunStatus.Failed);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Verifier failed for run {RunId}", RunId);
            Message = "Verifier error: " + ex.Message;
            await FinishAsync(RunStatus.Failed);
        }
    }

    private async Task FinishAsync(RunStatus status)
    {
        Status = status;
        EndedAt = _clock.GetUtcNow();
        _watching.Cancel();
        AddEvent(Guid.Empty, $"Run {status}");
        try
        {
            await _store.SaveEndAsync(this, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save run {RunId}", RunId);
        }
    }

    private void AddEvent(Guid auctionId, string text)
    {
        _events.Enqueue(new RunEvent { AtMs = _clock.GetUtcNow().ToUnixTimeMilliseconds(), AuctionId = auctionId == Guid.Empty ? "" : auctionId.ToString(), Text = text });
        while (_events.Count > 200)
        {
            _events.TryDequeue(out _);
        }
    }
}
