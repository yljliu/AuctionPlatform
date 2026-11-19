using Auction.Contracts;
using Auction.Contracts.Errors;
using Auction.Web.Services.Bidding;
using FluentAssertions;
using Grpc.Core;
using Xunit;

namespace Auction.Web.Tests;

/// <summary>Spec 06 task 6 (R3.6): one idempotency key per bid attempt, reused on every retry.</summary>
public class BidSenderTests
{
    private const string Auction = "a0000000-0000-0000-0000-00000000000a";

    /// <summary>Answers from a script: an exception to throw, or a reply. Remembers every request.</summary>
    internal sealed class FakeBidClient(params object[] script) : IBidClient
    {
        private readonly Queue<object> _script = new(script);

        public List<PlaceBidRequest> Requests { get; } = [];

        public Task<PlaceBidResponse> PlaceBidAsync(PlaceBidRequest request, DateTime deadline, CancellationToken ct)
        {
            Requests.Add(request.Clone());
            var next = _script.Count > 0 ? _script.Dequeue() : Leading();
            return next is Exception ex ? Task.FromException<PlaceBidResponse>(ex) : Task.FromResult((PlaceBidResponse)next);
        }
    }

    internal static PlaceBidResponse Leading() => new()
    {
        Result = PlaceBidResponse.Types.Result.Leading, CurrentPrice = new Domain.Money(12m, "USD").ToProto(),
    };

    internal static RpcException Unavailable() => new(new Status(StatusCode.Unavailable, "api down"));

    internal static RpcException Error(string code, string corr = "corr-7") => RpcErrors.Create(code, "No.", corr);

    private static BidSender Sender(FakeBidClient client) => new(client, TimeProvider.System) { Backoff = _ => TimeSpan.Zero };

    [Fact]
    public async Task Accepted_bid_sends_once_with_a_key()
    {
        var client = new FakeBidClient(Leading());

        var result = await Sender(client).SendAsync(Auction, 12m, "USD");

        result.Kind.Should().Be(BidSendKind.Accepted);
        client.Requests.Should().ContainSingle();
        Guid.TryParse(client.Requests[0].IdempotencyKey, out _).Should().BeTrue();
        client.Requests[0].MaxAmount.ToDomain().Amount.Should().Be(12m);
    }

    [Fact]
    public async Task Retries_reuse_the_same_key()
    {
        var client = new FakeBidClient(Unavailable(), Unavailable(), Leading());

        var result = await Sender(client).SendAsync(Auction, 12m, "USD");

        result.Kind.Should().Be(BidSendKind.Accepted);
        client.Requests.Should().HaveCount(3);
        client.Requests.Select(r => r.IdempotencyKey).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task No_answer_after_3_tries_is_unknown_and_the_manual_retry_reuses_the_key()
    {
        var client = new FakeBidClient(Unavailable(), Unavailable(), Unavailable(), Leading());
        var sender = Sender(client);

        var first = await sender.SendAsync(Auction, 12m, "USD");
        var retry = await sender.SendAsync(Auction, 12m, "USD");

        first.Kind.Should().Be(BidSendKind.Unknown);
        retry.Kind.Should().Be(BidSendKind.Accepted);
        client.Requests.Select(r => r.IdempotencyKey).Distinct().Should().ContainSingle("the retry is the same bid attempt");
    }

    [Fact]
    public async Task A_new_amount_gets_a_new_key()
    {
        var client = new FakeBidClient(Unavailable(), Unavailable(), Unavailable(), Leading());
        var sender = Sender(client);

        await sender.SendAsync(Auction, 12m, "USD");
        await sender.SendAsync(Auction, 15m, "USD");

        client.Requests[0].IdempotencyKey.Should().NotBe(client.Requests[^1].IdempotencyKey);
    }

    [Fact]
    public async Task After_success_the_next_bid_gets_a_new_key()
    {
        var client = new FakeBidClient(Leading(), Leading());
        var sender = Sender(client);

        await sender.SendAsync(Auction, 12m, "USD");
        await sender.SendAsync(Auction, 12m, "USD");

        client.Requests[0].IdempotencyKey.Should().NotBe(client.Requests[1].IdempotencyKey);
        sender.PendingKey.Should().BeNull();
    }

    [Fact]
    public async Task Rejection_is_not_retried_and_keeps_the_ref()
    {
        var client = new FakeBidClient(Error("BELOW_MINIMUM"));

        var result = await Sender(client).SendAsync(Auction, 1m, "USD");

        result.Kind.Should().Be(BidSendKind.Rejected);
        result.Error!.Code.Should().Be("BELOW_MINIMUM");
        result.Error.CorrelationId.Should().Be("corr-7");
        client.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Rate_limit_is_not_retried_automatically()
    {
        var client = new FakeBidClient(Error("RATE_LIMITED"));

        var result = await Sender(client).SendAsync(Auction, 12m, "USD");

        result.Kind.Should().Be(BidSendKind.Rejected);
        client.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Double_click_sends_one_bid_key()
    {
        var gate = new TaskCompletionSource<PlaceBidResponse>();
        var client = new SlowClient(gate.Task);
        var sender = new BidSender(client, TimeProvider.System);

        var first = sender.SendAsync(Auction, 12m, "USD");
        var second = sender.SendAsync(Auction, 12m, "USD");   // waits for the first
        gate.SetResult(Leading());
        await Task.WhenAll(first, second);

        client.Keys.Should().HaveCount(2, "the second click runs after the first finished");
        client.Concurrent.Should().Be(1, "never two at once");
    }

    private sealed class SlowClient(Task<PlaceBidResponse> answer) : IBidClient
    {
        private int _inFlight;

        public List<string> Keys { get; } = [];

        public int Concurrent { get; private set; }

        public async Task<PlaceBidResponse> PlaceBidAsync(PlaceBidRequest request, DateTime deadline, CancellationToken ct)
        {
            Concurrent = Math.Max(Concurrent, Interlocked.Increment(ref _inFlight));
            Keys.Add(request.IdempotencyKey);
            try
            {
                return await answer;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
