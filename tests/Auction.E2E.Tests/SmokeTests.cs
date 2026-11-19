using Microsoft.Playwright;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Auction.E2E.Tests;

/// <summary>
/// Spec 06 task 10: the Playwright smoke path. Sign in (quick login as alice) -> browse -> open the auction -> bid -> see "leading".
/// Needs the stack with seed data and Demo__Password set (compose passes SEED_PASSWORD), plus E2E_WEB1 and E2E_SQL.
/// </summary>
public class SmokeTests(ITestOutputHelper output)
{
    [E2EFact]
    public async Task Sign_in_browse_open_bid_and_lead()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(E2ESettings.Redis);
        var (auctionId, title, _) = await StackData.CreateLiveAuctionAsync(redis);
        output.WriteLine($"auction {auctionId} \"{title}\"");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        page.SetDefaultTimeout(30_000);

        // Sign in with the demo quick login.
        await page.GotoAsync($"{E2ESettings.Web1}/account/login");
        await page.SelectOptionAsync("#quick-user", "alice");
        await page.ClickAsync("#quick-login");
        await Expect(page.Locator("#app-avatar")).ToBeVisibleAsync();

        // Browse: search for the auction by its title, then open its card.
        await page.GotoAsync($"{E2ESettings.Web1}/c?q={Uri.EscapeDataString(title)}");
        var card = page.Locator($"#card-{auctionId}");
        await Expect(card).ToBeVisibleAsync();
        await card.Locator("a.auction-card-link").ClickAsync();
        await page.WaitForURLAsync($"**/a/{auctionId}");

        // Bid the minimum with the first quick-bid button.
        await Expect(page.Locator("#live-panel[data-feed='Live']")).ToBeVisibleAsync();
        var quick = page.Locator("#quick-bids button").First;
        await Expect(quick).ToBeEnabledAsync();
        await quick.ClickAsync();

        // Leading: the snackbar, my status and the leader name all say so.
        await Expect(page.GetByText("You're leading at")).ToBeVisibleAsync();
        await Expect(page.Locator("#my-status")).ToContainTextAsync("You're leading");
        await Expect(page.Locator("#live-leader")).ToHaveTextAsync("you");
        await Expect(page.Locator("#live-bid-count")).ToHaveTextAsync("1");
    }
}
