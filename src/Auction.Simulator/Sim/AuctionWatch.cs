using Auction.Contracts;
using Auction.Domain.Bidding;

namespace Auction.Simulator.Sim;

/// <summary>
/// One live subscription per target auction (design 1). It keeps what bots need to choose a bid, counts
/// extensions, and remembers the last seq for the verifier (R4.7). Reconnects with the last seq.
/// </summary>
public sealed class AuctionWatch(Guid auctionId, Action<Guid, string> onEvent)
{
    private readonly object _gate = new();

    public Guid AuctionId { get; } = auctionId;

    public long Seq { get; private set; }

    public long PriceC { get; private set; }

    public long MinNextC { get; private set; }

    public long StartC { get; private set; }

    public int BidCount { get; private set; }

    public Guid? Leader { get; private set; }

    public long EndMs { get; private set; }

    public string Status { get; private set; } = "";

    public IncrementTable? Increments { get; private set; }

    public long Extensions { get; private set; }

    public bool HasSnapshot { get; private set; }

    public bool IsOver => Status is "Sealed" or "Settling" or "Settled" or "Cancelled";

    public async Task RunAsync(IApiPort api, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var e in api.SubscribeAsync(AuctionId, Seq, ct))
                {
                    Apply(e);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // The stream broke (Api restart, network); try again from the last seq.
            }

            try
            {
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Apply(AuctionEvent e)
    {
        lock (_gate)
        {
            switch (e.BodyCase)
            {
                case AuctionEvent.BodyOneofCase.Snapshot:
                    var s = e.Snapshot;
                    Seq = s.Seq;
                    Status = s.Status;
                    PriceC = Cents(s.CurrentPrice);
                    StartC = Cents(s.StartingPrice);
                    MinNextC = Cents(s.MinNextBid);
                    BidCount = s.BidCount;
                    Leader = Guid.TryParse(s.Leader?.UserId, out var l) ? l : null;
                    EndMs = s.EndTimeMs;
                    if (s.IncrementTiers.Count > 0)
                    {
                        Increments = IncrementTable.FromMoney(s.IncrementTiers.Select(t => (t.From.ToDomain().Amount, t.Increment.ToDomain().Amount)));
                    }

                    HasSnapshot = true;
                    break;

                case AuctionEvent.BodyOneofCase.BidAccepted when e.Seq > Seq:
                    var b = e.BidAccepted;
                    Seq = e.Seq;
                    PriceC = Cents(b.Price);
                    MinNextC = b.MinNextBid == null ? MinNextC : Cents(b.MinNextBid);
                    BidCount = b.BidCount;
                    Leader = Guid.TryParse(b.Leader?.UserId, out var leader) ? leader : Leader;
                    if (b.EndTimeMs > 0)
                    {
                        EndMs = b.EndTimeMs;
                    }

                    if (b.Extended)
                    {
                        Extensions++;
                        onEvent(AuctionId, $"Extended to {DateTimeOffset.FromUnixTimeMilliseconds(EndMs):HH:mm:ss}");
                    }

                    break;

                case AuctionEvent.BodyOneofCase.StateChanged when e.Seq > Seq:
                    Seq = e.Seq;
                    Status = e.StateChanged.Status;
                    if (e.StateChanged.EndTimeMs > 0)
                    {
                        EndMs = e.StateChanged.EndTimeMs;
                    }

                    var change = string.IsNullOrEmpty(e.StateChanged.Change) ? Status : e.StateChanged.Change;
                    onEvent(AuctionId, string.IsNullOrEmpty(e.StateChanged.Outcome) ? change : $"{change}: {e.StateChanged.Outcome}");
                    break;
            }
        }
    }

    /// <summary>A consistent copy for one bid choice.</summary>
    public (string Status, long PriceC, long MinNextC, long StartC, Guid? Leader, long EndMs, IncrementTable? Increments) Read()
    {
        lock (_gate)
        {
            return (Status, PriceC, MinNextC, StartC, Leader, EndMs, Increments);
        }
    }

    private static long Cents(Money? m) => m == null ? 0 : IncrementTable.ToCents(m.ToDomain().Amount);
}

/// <summary>One bot: its tokens (refreshed before they run out) and its ceiling per auction.</summary>
public sealed class Bot(Guid userId, string username, AuthTokens tokens)
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Dictionary<Guid, long> _ceilings = [];
    private AuthTokens _tokens = tokens;

    public Guid UserId { get; } = userId;

    public string Username { get; } = username;

    public string AccessToken => _tokens.AccessToken;

    public long Ceiling(Guid auctionId, long startC, Random random)
    {
        lock (_ceilings)
        {
            if (!_ceilings.TryGetValue(auctionId, out var ceiling))
            {
                ceiling = BidChooser.Ceiling(startC, random);
                _ceilings[auctionId] = ceiling;
            }

            return ceiling;
        }
    }

    /// <summary>Refreshes when less than a minute is left (or when forced after UNAUTHENTICATED).</summary>
    public async Task EnsureFreshAsync(IApiPort api, long nowMs, CancellationToken ct, bool force = false)
    {
        if (!force && _tokens.AccessExpiresMs - nowMs > 60_000)
        {
            return;
        }

        await _refresh.WaitAsync(ct);
        try
        {
            if (!force && _tokens.AccessExpiresMs - nowMs > 60_000)
            {
                return;
            }

            _tokens = await api.RefreshAsync(_tokens.RefreshToken, ct);
        }
        finally
        {
            _refresh.Release();
        }
    }
}
