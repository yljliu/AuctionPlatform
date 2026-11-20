using Auction.Contracts;
using Auction.Domain.Errors;

namespace Auction.Simulator.Sim;

/// <summary>
/// Up to 3 active runs at once, one per scenario, so steady, closing-spike and snipe-war can run side by side.
/// Each scenario uses its own 500 bots and its own auctions, so every verifier checks only its own bids.
/// Keeps the last 20 runs in memory; older ones come from SQL.
/// </summary>
public sealed class RunManager(IApiPort api, IRunStore store, TimeProvider clock, ILogger<RunManager> log)
{
    public const int MaxBots = 500;

    /// <summary>bot_0001-0500 for steady, bot_0501-1000 for closing-spike, bot_1001-1500 for snipe-war.</summary>
    public static int FirstBotFor(string scenario) => scenario switch
    {
        Scenarios.ClosingSpike => MaxBots + 1,
        Scenarios.SnipeWar => (2 * MaxBots) + 1,
        _ => 1,
    };

    private readonly object _gate = new();
    private readonly List<Run> _recent = [];

    public Run? Active
    {
        get
        {
            lock (_gate)
            {
                return _recent.LastOrDefault(r => r.IsActive);
            }
        }
    }

    public IReadOnlyList<Run> ActiveRuns
    {
        get
        {
            lock (_gate)
            {
                return _recent.Where(r => r.IsActive).ToList();
            }
        }
    }

    public Run Start(RunConfig config, Guid adminId, string adminToken)
    {
        Validate(config);
        lock (_gate)
        {
            if (_recent.Any(r => r.IsActive && r.Config.Scenario == config.Scenario))
            {
                throw new DomainException(ErrorCodes.RunAlreadyActive, $"A {config.Scenario} run is already going. Stop it first.");
            }

            Run? self = null;
            var run = new Run(config, adminId, adminToken, api, store, clock, log)
            {
                FirstBot = FirstBotFor(config.Scenario),
                BusyAuctions = () => ActiveRuns.Where(r => !ReferenceEquals(r, self)).SelectMany(r => r.Targets).ToHashSet(),
            };
            self = run;
            _recent.Add(run);
            if (_recent.Count > 20)
            {
                _recent.RemoveAt(0);
            }

            run.Start();
            return run;
        }
    }

    /// <summary>The run by id; an empty id means the active run, else the newest one.</summary>
    public Run? Find(string? runId)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(runId))
            {
                return _recent.LastOrDefault(r => r.IsActive) ?? _recent.LastOrDefault();
            }

            return Guid.TryParse(runId, out var id) ? _recent.FirstOrDefault(r => r.RunId == id) : null;
        }
    }

    public Run Get(string? runId) =>
        Find(runId) ?? throw new DomainException(ErrorCodes.NotFound, "That run was not found (it may be from before a restart).");

    public async Task<IReadOnlyList<RunInfo>> ListAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        List<RunInfo> mine;
        lock (_gate)
        {
            mine = _recent.AsEnumerable().Reverse().Select(r => r.Info()).ToList();
        }

        var saved = await store.ListAsync(limit, ct);
        return mine.Concat(saved.Where(s => mine.All(m => m.RunId != s.RunId))).Take(limit).ToList();
    }

    public static void Validate(RunConfig c)
    {
        var issues = new List<FieldIssue>();
        if (!Scenarios.IsKnown(c.Scenario))
        {
            issues.Add(new("scenario", "one_of", "Use steady, closing-spike or snipe-war."));
        }

        if (c.Bots is < 1 or > MaxBots)
        {
            issues.Add(new("bots", "range", $"Use 1 to {MaxBots} bots."));
        }

        if (c.RatePerAuction is <= 0 or > 50)
        {
            issues.Add(new("rate_per_auction", "range", "Use more than 0 and at most 50 bids per second per auction."));
        }

        if (c.Aggressiveness is < 1 or > 5)
        {
            issues.Add(new("aggressiveness", "range", "Use 1 to 5."));
        }

        if (Scenarios.MakesAuctions(c.Scenario))
        {
            if (c.DemoAuctions is < 1 or > 100)
            {
                issues.Add(new("demo_auctions", "range", "Use 1 to 100 auctions."));
            }

            if (c.DemoMinutes is < 1 or > 60)
            {
                issues.Add(new("demo_minutes", "range", "Use 1 to 60 minutes."));
            }
        }
        else if (c.DurationSec is < 5 or > 3600)
        {
            issues.Add(new("duration_sec", "range", "Use 5 to 3600 seconds."));
        }

        if (issues.Count > 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Check the run settings.", issues);
        }
    }
}
