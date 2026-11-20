using System.Runtime.CompilerServices;
using Auction.Contracts;
using Grpc.Core;
using Grpc.Net.Client;

namespace Auction.Simulator.Sim;

/// <summary>
/// Everything the simulator asks the Api, behind one interface so runs can be tested without it.
/// Bots use only the public gRPC calls, with real tokens (R1.2).
/// </summary>
public interface IApiPort
{
    Task<IReadOnlyList<BotSession>> CreateBotsAsync(string adminToken, int count, CancellationToken ct);

    /// <summary>Bots bot_{first}..bot_{first+count-1}, so runs side by side use different bots. Fakes only need first = 1.</summary>
    Task<IReadOnlyList<BotSession>> CreateBotsAsync(string adminToken, int first, int count, CancellationToken ct) =>
        first == 1 ? CreateBotsAsync(adminToken, count, ct) : throw new NotSupportedException("This port only makes bots from bot_0001.");

    Task<IReadOnlyList<Guid>> CreateDemoAuctionsAsync(string adminToken, CreateDemoAuctionsRequest request, CancellationToken ct);

    Task<IReadOnlyList<Guid>> ListLiveAuctionsAsync(int max, CancellationToken ct);

    Task<PlaceBidResponse> PlaceBidAsync(string accessToken, PlaceBidRequest request, CancellationToken ct);

    Task<AuthTokens> RefreshAsync(string refreshToken, CancellationToken ct);

    IAsyncEnumerable<AuctionEvent> SubscribeAsync(Guid auctionId, long lastSeenSeq, CancellationToken ct);
}

/// <summary>
/// The real port: one HTTP/2 channel per Api copy (many calls share it), picked round robin (design 1).
/// </summary>
public sealed class GrpcApiPort : IApiPort, IDisposable
{
    private readonly GrpcChannel[] _channels;
    private int _next;

    public GrpcApiPort(IEnumerable<string> endpoints)
    {
        _channels = endpoints.Select(e => GrpcChannel.ForAddress(e, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true, PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5) },
        })).ToArray();
        if (_channels.Length == 0)
        {
            throw new InvalidOperationException("No Api endpoints configured (Api__Endpoints__0).");
        }
    }

    private GrpcChannel Channel => _channels[(int)((uint)Interlocked.Increment(ref _next) % _channels.Length)];

    private static Metadata Bearer(string token) => [new Metadata.Entry("authorization", "Bearer " + token)];

    public Task<IReadOnlyList<BotSession>> CreateBotsAsync(string adminToken, int count, CancellationToken ct) =>
        CreateBotsAsync(adminToken, 1, count, ct);

    public async Task<IReadOnlyList<BotSession>> CreateBotsAsync(string adminToken, int first, int count, CancellationToken ct) =>
        (await new AdminService.AdminServiceClient(Channel).CreateBotsAsync(new CreateBotsRequest { Count = count, First = first }, Bearer(adminToken),
            deadline: DateTime.UtcNow.AddMinutes(2), cancellationToken: ct)).Bots.ToList();

    public async Task<IReadOnlyList<Guid>> CreateDemoAuctionsAsync(string adminToken, CreateDemoAuctionsRequest request, CancellationToken ct) =>
        (await new AdminService.AdminServiceClient(Channel).CreateDemoAuctionsAsync(request, Bearer(adminToken),
            deadline: DateTime.UtcNow.AddMinutes(1), cancellationToken: ct)).AuctionIds.Select(Guid.Parse).ToList();

    public async Task<IReadOnlyList<Guid>> ListLiveAuctionsAsync(int max, CancellationToken ct) =>
        (await new CatalogService.CatalogServiceClient(Channel).ListAuctionsAsync(
            new ListAuctionsRequest { Filter = new AuctionFilter { Status = "Live" }, PageSize = Math.Clamp(max, 1, 100) }, cancellationToken: ct))
        .Auctions.Select(a => Guid.Parse(a.AuctionId)).ToList();

    public async Task<PlaceBidResponse> PlaceBidAsync(string accessToken, PlaceBidRequest request, CancellationToken ct) =>
        await new BiddingService.BiddingServiceClient(Channel).PlaceBidAsync(request, Bearer(accessToken),
            deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);

    public async Task<AuthTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        (await new AuthService.AuthServiceClient(Channel).RefreshTokenAsync(new RefreshTokenRequest { RefreshToken = refreshToken }, cancellationToken: ct)).Tokens;

    public async IAsyncEnumerable<AuctionEvent> SubscribeAsync(Guid auctionId, long lastSeenSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        using var call = new LiveService.LiveServiceClient(Channel).SubscribeAuction(
            new SubscribeAuctionRequest { AuctionId = auctionId.ToString(), LastSeenSeq = lastSeenSeq }, cancellationToken: ct);
        await foreach (var e in call.ResponseStream.ReadAllAsync(ct))
        {
            yield return e;
        }
    }

    public void Dispose()
    {
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }
}
