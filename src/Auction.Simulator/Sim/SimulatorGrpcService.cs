using Auction.Contracts;
using Auction.Contracts.Errors;
using Auction.Domain.Errors;
using Auction.Infrastructure.Identity;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Auction.Simulator.Sim;

/// <summary>
/// Admin token only, on every call (design 1: the same signing key as the Api). DomainException becomes the
/// usual ErrorDetail, so the admin page shows the same messages as for the Api.
/// </summary>
public sealed class AdminOnlyInterceptor : Interceptor
{
    private static void Check(ServerCallContext context)
    {
        var user = context.GetHttpContext().User;
        if (user.Identity?.IsAuthenticated != true)
        {
            throw new DomainException(ErrorCodes.Unauthenticated, "Please sign in.");
        }

        if (!user.HasClaim(AuthClaims.Role, "Admin"))
        {
            throw new DomainException(ErrorCodes.RoleRequired, "Only admins can run the simulator.");
        }
    }

    private static string? Correlation(ServerCallContext context) => context.RequestHeaders.GetValue(RpcErrors.CorrelationHeader);

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            Check(context);
            return await continuation(request, context);
        }
        catch (DomainException ex)
        {
            throw RpcErrors.From(ex, Correlation(context));
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            Check(context);
            await continuation(request, responseStream, context);
        }
        catch (DomainException ex)
        {
            throw RpcErrors.From(ex, Correlation(context));
        }
    }
}

/// <summary>SimulatorService (spec 08 task 2, R3).</summary>
public sealed class SimulatorGrpcService(RunManager runs) : SimulatorService.SimulatorServiceBase
{
    public override Task<ListScenariosResponse> ListScenarios(Empty request, ServerCallContext context)
    {
        var response = new ListScenariosResponse();
        response.Scenarios.AddRange(Scenarios.All.Select(s => new Scenario { Name = s.Name, Description = s.Description }));
        return Task.FromResult(response);
    }

    public override Task<RunInfo> StartRun(StartRunRequest request, ServerCallContext context)
    {
        var user = context.GetHttpContext().User;
        var adminId = Guid.TryParse(user.FindFirst(AuthClaims.Subject)?.Value, out var id) ? id : Guid.Empty;
        var token = (context.RequestHeaders.GetValue("authorization") ?? "").Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();

        var targets = new List<Guid>();
        foreach (var t in request.TargetAuctionIds)
        {
            targets.Add(Guid.TryParse(t, out var g) ? g
                : throw new DomainException(ErrorCodes.ValidationFailed, "A target is not a valid auction id.", [new FieldIssue("target_auction_ids", "format", "Must be UUIDs.")]));
        }

        var run = runs.Start(new RunConfig(
            request.Scenario.Trim(), request.Bots, request.RatePerAuction, targets, request.DemoAuctions, request.DemoMinutes,
            request.DurationSec, request.Aggressiveness == 0 ? 2 : request.Aggressiveness, request.Seed), adminId, token);
        return Task.FromResult(run.Info());
    }

    public override Task<RunInfo> StopRun(StopRunRequest request, ServerCallContext context)
    {
        var run = runs.Get(request.RunId);
        run.Stop();
        return Task.FromResult(run.Info());
    }

    public override async Task<RunInfo> AdjustRun(AdjustRunRequest request, ServerCallContext context)
    {
        var run = runs.Get(request.RunId);
        if (!run.IsActive || run.BotsStopped)
        {
            throw new DomainException(ErrorCodes.InvalidState, "That run is not bidding any more.");
        }

        if (request.Bots is < 0 or > RunManager.MaxBots || request.RatePerAuction is < 0 or > 50)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Use 1 to 500 bots and at most 50 bids per second per auction.");
        }

        await run.AdjustAsync(request.Bots, request.RatePerAuction, context.CancellationToken);
        return run.Info();
    }

    public override Task<RunInfo> GetRun(GetRunRequest request, ServerCallContext context) =>
        Task.FromResult(runs.Get(request.RunId).Info());

    public override async Task<ListRunsResponse> ListRuns(ListRunsRequest request, ServerCallContext context)
    {
        var response = new ListRunsResponse();
        response.Runs.AddRange(await runs.ListAsync(request.Limit, context.CancellationToken));
        return response;
    }

    /// <summary>One message a second until the run is finished (R3.5), then the last one.</summary>
    public override async Task StreamRunMetrics(GetRunRequest request, IServerStreamWriter<RunMetrics> responseStream, ServerCallContext context)
    {
        var run = runs.Get(request.RunId);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            await responseStream.WriteAsync(run.Metrics(), context.CancellationToken);
            if (!run.IsActive)
            {
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(context.CancellationToken));
    }
}
