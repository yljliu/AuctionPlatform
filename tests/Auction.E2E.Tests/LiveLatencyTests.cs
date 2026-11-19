using System.Diagnostics;
using Auction.Domain.Bidding;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Redis;
using FluentAssertions;
using Microsoft.Playwright;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Auction.E2E.Tests;

/// <summary>Runs only when E2E_WEB1 is set, because it needs the running stack and a Playwright browser.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("E2E_WEB1")))
        {
            Skip = "Set E2E_WEB1 (and E2E_WEB2, E2E_SQL) to run the browser tests against a running stack.";
        }
    }
}

/// <summary>
/// Spec 04 task 9 (R3.2): a bid shows in two browsers, one on web-1 and one on web-2, within 500 ms.
/// The test makes a Live auction in SQL and Redis (StackData) and bids through bid.lua, so it needs no
/// signed-in user. That is the same path a real bid takes after the Api accepts it.
/// Needs E2E_WEB1 (and E2E_WEB2), E2E_REDIS and E2E_SQL.
/// </summary>
public class LiveLatencyTests(ITestOutputHelper output)
{
    private const int Bids = 5;
    private const int LimitMs = 500;

    [E2EFact]
    public async Task A_bid_shows_on_both_web_copies_within_500_ms()
    {
        var web1 = E2ESettings.Web1;
        var web2 = E2ESettings.Web2;

        using var redis = await ConnectionMultiplexer.ConnectAsync(E2ESettings.Redis);
        var (_, _, keys) = await StackData.CreateLiveAuctionAsync(redis);
        var script = new BidScript(new ScriptRegistry(redis));

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page1 = await OpenAsync(browser, $"{web1}/a/{keys.AuctionId}");
        var page2 = await OpenAsync(browser, $"{web2}/a/{keys.AuctionId}");

        var worst = 0L;
        for (var i = 1; i <= Bids; i++)
        {
            var bidder = Guid.NewGuid();
            var maxC = 1000L + (i * 10_000);
            var clock = Stopwatch.StartNew();

            var outcome = await script.RunAsync(keys, bidder, maxC, BidAmountRules.Fingerprint(bidder, maxC, "USD"), Guid.NewGuid(), "USD", "e2e");
            outcome.Accepted.Should().BeTrue($"bid {i} should be accepted");

            var count = i.ToString();
            await Task.WhenAll(
                Expect(page1.Locator("#live-bid-count")).ToHaveTextAsync(count, new() { Timeout = LimitMs }),
                Expect(page2.Locator("#live-bid-count")).ToHaveTextAsync(count, new() { Timeout = LimitMs }));

            output.WriteLine($"bid {i}: both pages updated after {clock.ElapsedMilliseconds} ms");
            worst = Math.Max(worst, clock.ElapsedMilliseconds);
        }

        worst.Should().BeLessThan(LimitMs);
    }

    // A fresh browser context per page, so the two pages share no cookies (like two people).
    private static async Task<IPage> OpenAsync(IBrowser browser, string url)
    {
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(url);

        // Caught up and live: the snapshot is shown and the feed says Live (signed out, so no bid form).
        await Expect(page.Locator("#live-bid-count")).ToHaveTextAsync("0", new() { Timeout = 30_000 });
        await Expect(page.Locator("#live-panel[data-feed='Live']")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        return page;
    }
}
