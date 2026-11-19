using Microsoft.Extensions.Logging;

namespace Auction.Infrastructure.Lifecycle;

/// <summary>Lease settings (R1.1). Holder must be unique per running process.</summary>
public sealed class LeaseOptions
{
    public string LeaseName { get; set; } = "lifecycle";

    public string Holder { get; set; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan RenewEvery { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Stop being leader this long before the lease could expire in SQL, in case clocks drift.</summary>
    public TimeSpan SafetyMargin { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Keeps the lifecycle lease (spec 05 task 1). Only the copy holding it does lifecycle work.
/// IsLeader turns false at once when a renew fails, and also when no renew has worked for a whole TTL.
/// </summary>
public sealed class LeaseManager(ILeaseStore store, LeaseOptions options, TimeProvider clock, ILogger<LeaseManager> log)
{
    private readonly object _gate = new();
    private LeaseGrant? _grant;
    private DateTimeOffset _validUntil;

    public string Holder => options.Holder;

    /// <summary>The lease we hold now, or null. Leader work takes this once and passes it to Fencing.</summary>
    public LeaseGrant? Current
    {
        get
        {
            lock (_gate)
            {
                return _grant != null && clock.GetUtcNow() < _validUntil ? _grant : null;
            }
        }
    }

    public bool IsLeader => Current != null;

    /// <summary>The current fencing token, or 0 when not leader.</summary>
    public long FencingToken => Current?.Token ?? 0;

    /// <summary>One acquire or renew. Returns true when we hold the lease afterwards.</summary>
    public async Task<bool> RenewOnceAsync(CancellationToken ct = default)
    {
        // Count the lease from before the call: SQL set its expiry no earlier than this.
        var askedAt = clock.GetUtcNow();
        long? token;
        try
        {
            token = await store.TryAcquireAsync(options.LeaseName, options.Holder, options.Ttl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Lose(ex, "renew failed");
            return false;
        }

        if (token is null)
        {
            Lose(null, "another copy holds the lease");
            return false;
        }

        bool gained;
        lock (_gate)
        {
            gained = _grant == null || _grant.Token != token.Value;
            _grant = new LeaseGrant(options.LeaseName, options.Holder, token.Value);
            _validUntil = askedAt + options.Ttl - options.SafetyMargin;
        }

        if (gained)
        {
            log.LogInformation("Became lifecycle leader as {Holder} with fencing token {Token}", options.Holder, token.Value);
        }

        return true;
    }

    /// <summary>Renews every RenewEvery until stopped, then gives the lease up so another copy can take over at once.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RenewOnceAsync(ct);
                await Task.Delay(options.RenewEvery, clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
        finally
        {
            await ReleaseAsync();
        }
    }

    /// <summary>Stops being leader and lets the lease expire in SQL.</summary>
    public async Task ReleaseAsync()
    {
        bool held;
        lock (_gate)
        {
            held = _grant != null;
            _grant = null;
        }

        if (!held)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await store.ReleaseAsync(options.LeaseName, options.Holder, timeout.Token);
            log.LogInformation("Released the lifecycle lease held by {Holder}", options.Holder);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not release the lifecycle lease; it expires by itself in {Ttl}", options.Ttl);
        }
    }

    private void Lose(Exception? error, string reason)
    {
        bool was;
        lock (_gate)
        {
            was = _grant != null;
            _grant = null;
        }

        if (was)
        {
            log.LogWarning(error, "Lost the lifecycle lease ({Reason}); lifecycle work stops now", reason);
        }
        else if (error != null)
        {
            log.LogDebug(error, "Lease try failed ({Reason})", reason);
        }
    }
}
