using Auction.Web.Services.Admin;
using FluentAssertions;
using Xunit;

namespace Auction.Web.Tests;

/// <summary>"Start all 3" on the admin page starts every idle scenario, steady first.</summary>
public class SimulatorScenariosTests
{
    [Fact]
    public void All_three_start_when_none_run() =>
        SimulatorScenarios.ToStart(new Dictionary<string, bool>()).Should().Equal("steady", "closing-spike", "snipe-war");

    [Fact]
    public void Running_ones_are_left_alone() =>
        SimulatorScenarios.ToStart(new Dictionary<string, bool> { ["closing-spike"] = true, ["steady"] = false })
            .Should().Equal("steady", "snipe-war");

    [Fact]
    public void Nothing_to_start_when_all_run() =>
        SimulatorScenarios.ToStart(SimulatorScenarios.All.ToDictionary(s => s, _ => true)).Should().BeEmpty();
}
