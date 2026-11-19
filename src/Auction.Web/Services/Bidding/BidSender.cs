using Auction.Contracts;
using Auction.Web.Services.Ui;
using Grpc.Core;

namespace Auction.Web.Services.Bidding;

/// <summary>The PlaceBid call, behind an interface so BidSender can be tested without the Api.</summary>
public interface IBidClient
{
    Task<PlaceBidResponse> PlaceBidAsync(PlaceBidRequest request, DateTime deadline, CancellationToken ct);
}

public sealed class GrpcBidClient(BiddingService.BiddingServiceClient client) : IBidClient
{
    public async Task<PlaceBidResponse> PlaceBidAsync(PlaceBidRequest request, DateTime deadline, CancellationToken ct) =>
        await client.PlaceBidAsync(request, deadline: deadline, cancellationToken: ct);
}

public enum BidSendKind
{
    /// <summary>The Api accepted the bid (Leading, OutbidByProxy or MaxRaised).</summary>
    Accepted,

    /// <summary>The Api said no (below minimum, ended, ...). The next bid gets a new key.</summary>
    Rejected,

    /// <summary>No answer after 3 tries. Retrying sends the same key, so it can never count twice.</summary>
    Unknown,
}

public sealed record BidSendResult(BidSendKind Kind, PlaceBidResponse? Response = null, UiError? Error = null);

/// <summary>
/// Sends bids for one page (scoped per circuit). One idempotency key per bid attempt: automatic retries
/// and a manual "Retry" after Unknown reuse it, so the Api counts the bid at most once (R3.6, design 3).
/// </summary>
public sealed class BidSender(IBidClient client, TimeProvider clock)
{
    public const int Attempts = 3;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Guid? _pendingKey;
    private string? _pendingAuction;
    private decimal? _pendingAmount;

    /// <summary>Wait time before retry n (0-based): 200 ms, 400 ms, 800 ms, each plus up to 100 ms jitter.</summary>
    public Func<int, TimeSpan> Backoff { get; set; } = attempt => TimeSpan.FromMilliseconds((200 << attempt) + Random.Shared.Next(0, 100));

    /// <summary>The key a retry would reuse (tests and logs).</summary>
    public Guid? PendingKey => _pendingKey;

    public async Task<BidSendResult> SendAsync(string auctionId, decimal max, string currency, CancellationToken ct = default)
    {
        // A double click waits here and then sends the same key, so it can only ever be one bid.
        await _gate.WaitAsync(ct);
        try
        {
            if (_pendingKey == null || _pendingAmount != max || _pendingAuction != auctionId)
            {
                _pendingKey = Guid.NewGuid();
                _pendingAmount = max;
                _pendingAuction = auctionId;
            }

            var request = new PlaceBidRequest
            {
                AuctionId = auctionId,
                MaxAmount = UiFormat.ToProto(max, currency),
                IdempotencyKey = _pendingKey.ToString(),
            };

            UiError? last = null;
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                try
                {
                    var reply = await client.PlaceBidAsync(request, clock.GetUtcNow().UtcDateTime + Deadline, ct);
                    Clear();
                    return new BidSendResult(BidSendKind.Accepted, reply);
                }
                catch (RpcException ex) when (IsRetryable(ex))
                {
                    last = UiError.From(ex);
                    if (attempt < Attempts - 1)
                    {
                        await Task.Delay(Backoff(attempt), clock, ct);
                    }
                }
                catch (RpcException ex)
                {
                    Clear();
                    return new BidSendResult(BidSendKind.Rejected, Error: UiError.From(ex));
                }
            }

            return new BidSendResult(BidSendKind.Unknown, Error: last);   // keep the key for the manual retry
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Unavailable, deadline, or an ErrorDetail marked retryable (but never a rate limit: wait for the user).</summary>
    public static bool IsRetryable(RpcException ex)
    {
        var error = UiError.From(ex);
        return error.Code != "RATE_LIMITED" && error.Retryable;
    }

    private void Clear()
    {
        _pendingKey = null;
        _pendingAmount = null;
        _pendingAuction = null;
    }
}
