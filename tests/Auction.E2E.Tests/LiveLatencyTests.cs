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
            Skip = "Set E2E_WEB1 (and E2E_WEB2) to run the browser tests against a running stack.";
        }
    }
}

/// <summary>
/// Spec 04 task 9 (R3.2): a bid shows in two browsers, one on web-1 and one on web-2, within 500 ms.
/// The test puts a Live auction straight into Redis and bids through bid.lua, so it needs no seller,
/// no SQL row and no bid panel. That is the same path a real bid takes after the Api accepts it.
/// </summary>
public class LiveLatencyTests(ITestOutputHelper output)
{
    private const int Bids = 5;
    private const int LimitMs = 500;

    [E2EFact]
    public async Task A_bid_shows_on_both_web_copies_within_500_ms()
    {
        var web1 = Setting("E2E_WEB1", "http://localhost:5001");
        var web2 = Setting("E2E_WEB2", web1);
        var partitions = int.Parse(Setting("E2E_PARTITIONS", "1"));

        using var redis = await ConnectionMultiplexer.ConnectAsync(Setting("E2E_REDIS", "localhost:6379"));
        var keys = await SeedLiveAuctionAsync(redis.GetDatabase(), Guid.NewGuid(), partitions);
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

        // Caught up and live: the snapshot is shown and the bid button is on.
        await Expect(page.Locator("#live-bid-count")).ToHaveTextAsync("0", new() { Timeout = 30_000 });
        await Expect(page.Locator("#live-bid-button")).ToBeEnabledAsync(new() { Timeout = 30_000 });
        return page;
    }

    // Same Redis fields the auction start job (spec 05) will write. Expires after an hour.
    private static async Task<AuctionKeys> SeedLiveAuctionAsync(IDatabase db, Guid auctionId, int partitions)
    {
        var keys = RedisKeys.For(auctionId, partitions);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.HashSetAsync(keys.State,
        [
            new("status", "Live"), new("seller", Guid.NewGuid().ToString()), new("startMs", now - 60_000),
            new("endMs", now + 600_000), new("startC", 1000), new("priceC", 0), new("leader", ""),
            new("leaderMaxC", 0), new("bidCount", 0), new("seq", 0), new("eseq", 0), new("persistedEseq", 0),
            new("snipeMs", 0), new("extMs", 0), new("maxExtMs", 0), new("extUsedMs", 0),
            new("inc", "0:100,5000:250,25000:500,100000:1000,500000:5000"), new("cur", "USD"),
        ]);
        await db.KeyExpireAsync(keys.State, TimeSpan.FromHours(1));
        return keys;
    }

    private static string Setting(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.TrimEnd('/');
    }
}
