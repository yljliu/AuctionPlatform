namespace Auction.Web.Services.Admin;

/// <summary>The 3 simulator scenarios shown side by side on the admin page. Pure, so it is unit tested.</summary>
public static class SimulatorScenarios
{
    public static readonly IReadOnlyList<string> All = ["steady", "closing-spike", "snipe-war"];

    /// <summary>
    /// The scenarios "Start all 3" should start, in order: the idle ones. Steady goes first, so it picks the Live auctions
    /// before the other two make theirs; the simulator also keeps steady off auctions another run already uses.
    /// </summary>
    public static IReadOnlyList<string> ToStart(IReadOnlyDictionary<string, bool> running) =>
        All.Where(s => !running.GetValueOrDefault(s)).ToList();
}
