using Auction.Contracts;
using Auction.Contracts.Errors;
using Grpc.Core;
using Grpc.Net.Client;

namespace Auction.Simulator;

/// <summary>
/// Spec 08 task 8 check by hand: signs in as the seeded admin, starts a closing-spike run through the
/// Simulator service (the same calls the admin page makes), follows it, and prints PASS or FAIL.
///
///   dotnet run --project src/Auction.Simulator -- sim-check [bots=200] [auctions=20] [minutes=5] [ratePerAuction=5]
///
/// Needs: SEED_PASSWORD (the demo users' password), and the stack's gRPC ports on this PC:
///   Api at SIM_API (default http://localhost:7001), Simulator at SIM_SIMULATOR (default http://localhost:7201).
/// PASS means the verifier passed and the last P99 bid latency was under 350 ms (spec 08 task 8).
/// </summary>
public static class SimCheck
{
    public const double P99LimitMs = 350;

    public static async Task<int> RunAsync(string[] args)
    {
        var bots = Arg(args, 1, 200);
        var auctions = Arg(args, 2, 20);
        var minutes = Arg(args, 3, 5);
        var rate = Arg(args, 4, 5);
        var password = Environment.GetEnvironmentVariable("SEED_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Set SEED_PASSWORD (the password the demo users were seeded with).");
            return 2;
        }

        using var api = GrpcChannel.ForAddress(Environment.GetEnvironmentVariable("SIM_API") ?? "http://localhost:7001");
        using var simulator = GrpcChannel.ForAddress(Environment.GetEnvironmentVariable("SIM_SIMULATOR") ?? "http://localhost:7201");

        try
        {
            var login = await new AuthService.AuthServiceClient(api).LoginAsync(new LoginRequest { Email = "admin@demo.local", Password = password });
            var headers = new Metadata { { "authorization", "Bearer " + login.Tokens.AccessToken } };
            var client = new SimulatorService.SimulatorServiceClient(simulator);

            Say($"Starting closing-spike: {bots} bots, {auctions} auctions, {minutes} minutes, {rate} bids/s per auction at full rate.");
            var run = await client.StartRunAsync(new StartRunRequest
            {
                Scenario = "closing-spike", Bots = bots, RatePerAuction = rate, DemoAuctions = auctions, DemoMinutes = minutes, Aggressiveness = 2,
            }, headers);

            RunMetrics? last = null;
            var lastPrint = DateTime.MinValue;
            using var call = client.StreamRunMetrics(new GetRunRequest { RunId = run.RunId }, headers);
            await foreach (var m in call.ResponseStream.ReadAllAsync())
            {
                last = m;
                if (DateTime.UtcNow - lastPrint > TimeSpan.FromSeconds(10) || m.Status is not ("Running" or "Starting"))
                {
                    lastPrint = DateTime.UtcNow;
                    Say($"{m.Status,-9} sent {m.BidsSent,8:N0}  accepted {m.Accepted,8:N0}  rejected {m.Rejected,6:N0}  {m.BidsPerSec,6:0.#}/s  P50 {m.P50Ms,6:0.#} ms  P99 {m.P99Ms,6:0.#} ms  bots {m.ActiveBots}  extensions {m.Extensions}");
                }
            }

            var done = await client.GetRunAsync(new GetRunRequest { RunId = run.RunId }, headers);
            Say($"Run {done.Status}: {done.Message}");
            Console.WriteLine(done.ReportJson);
            var p99 = last?.P99Ms ?? 0;
            var verified = done.Status == "Passed";
            var fast = p99 < P99LimitMs;
            Say($"  {(verified ? "PASS" : "FAIL")}  verifier");
            Say($"  {(fast ? "PASS" : "FAIL")}  P99 bid latency {p99:0.#} ms (limit {P99LimitMs:0} ms)");
            return verified && fast ? 0 : 1;
        }
        catch (RpcException ex)
        {
            Console.Error.WriteLine($"FAIL: {RpcErrors.MessageOf(ex)} ({RpcErrors.CodeOf(ex)})");
            return 1;
        }
    }

    private static int Arg(string[] args, int index, int fallback) => args.Length > index && int.TryParse(args[index], out var n) ? n : fallback;

    private static void Say(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
}
