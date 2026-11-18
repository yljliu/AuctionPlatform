using System.Reflection;
using System.Text.RegularExpressions;
using Auction.Contracts.Errors;
using Auction.Domain.Bidding;
using Auction.Domain.Errors;
using Auction.Infrastructure.Bidding;
using Auction.Infrastructure.Redis;
using FluentAssertions;
using Grpc.Core;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>
/// Spec 03 tasks 4 and 6 without Redis: reading bid.lua's answers, the script file itself,
/// and the min next bid in the error sent to the caller.
/// </summary>
public class BidScriptUnitTests
{
    // ---------- BidOutcome.Parse ----------

    [Fact]
    public void Reads_an_accepted_outcome()
    {
        var outcome = BidOutcome.Parse("ok|Leading|5250|5500|7|1763400000000|1|7|1", replayed: false);

        outcome.Accepted.Should().BeTrue();
        outcome.Result.Should().Be(BidResult.Leading);
        outcome.PriceC.Should().Be(5250);
        outcome.MinNextC.Should().Be(5500);
        outcome.Seq.Should().Be(7);
        outcome.EndMs.Should().Be(1763400000000);
        outcome.Extended.Should().BeTrue();
        outcome.BidCount.Should().Be(7);
        outcome.IsLeader.Should().BeTrue();
        outcome.Replayed.Should().BeFalse();
    }

    [Theory]
    [InlineData("OutbidByProxy")]
    [InlineData("MaxRaised")]
    public void Reads_every_result(string result)
    {
        BidOutcome.Parse($"ok|{result}|100|200|1|0|0|1|0", replayed: true).Result.Should().Be(Enum.Parse<BidResult>(result));
    }

    [Fact]
    public void Reads_a_rejection_with_its_min_next()
    {
        var outcome = BidOutcome.Parse("err|BELOW_MINIMUM|1100", replayed: true);

        outcome.Accepted.Should().BeFalse();
        outcome.ErrorCode.Should().Be("BELOW_MINIMUM");
        outcome.MinNextC.Should().Be(1100);
        outcome.Replayed.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok")]
    [InlineData("ok|Leading|1|2|3|4|0|1")]          // one field short
    [InlineData("ok|Winning|1|2|3|4|0|1|1")]        // unknown result
    [InlineData("ok|Leading|x|2|3|4|0|1|1")]        // not a number
    [InlineData("ok|Leading|1|2|3|4|2|1|1")]        // flag must be 0 or 1
    [InlineData("err||0")]
    [InlineData("maybe|Leading|0")]
    public void Unreadable_outcomes_throw(string? text)
    {
        var act = () => BidOutcome.Parse(text, replayed: false);
        act.Should().Throw<FormatException>();
    }

    // ---------- the bid.lua file ----------

    private static string Script() => LuaScripts.Load(BidScript.FileName);

    [Fact]
    public void Script_is_embedded_in_the_infrastructure_dll()
    {
        Script().Should().Contain("redis.call('XADD', KEYS[6]");
    }

    [Fact]
    public void Script_uses_exactly_six_keys()
    {
        var used = Regex.Matches(Script(), @"KEYS\[(\d+)\]").Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order();

        used.Should().Equal(1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public void Every_code_the_script_returns_is_handled()
    {
        var known = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetValue(null)!)
            .Concat([BidOutcome.Missing, BidOutcome.CurrencyMismatch, BidOutcome.BadState])
            .ToHashSet();

        var returned = Regex.Matches(Script(), @"reject\('([A-Z_]+)'|'err\|([A-Z_]+)\|")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .Distinct()
            .ToList();

        returned.Should().Contain(["BELOW_MINIMUM", "NOT_LIVE", "ENDED", "OWN_LOT", "AUCTION_FROZEN", "IDEMPOTENCY_MISMATCH"]);
        returned.Should().OnlyContain(code => known.Contains(code));
    }

    [Fact]
    public void Script_uses_only_redis_time()
    {
        // R2.3: no os.time or other local clocks.
        Script().Should().Contain("redis.call('TIME')").And.NotContain("os.time").And.NotContain("os.clock");
    }

    [Fact]
    public void Script_never_puts_a_max_in_public_rows_or_events()
    {
        var script = Script();
        var rowJson = script[script.IndexOf("local json = ", StringComparison.Ordinal)..script.IndexOf("all[#all + 1]", StringComparison.Ordinal)];
        var eventJson = script[script.IndexOf("local event = ", StringComparison.Ordinal)..script.IndexOf("redis.call('ZADD', KEYS[4]", StringComparison.Ordinal)];

        rowJson.Should().NotContainEquivalentOf("max");
        eventJson.Should().NotContainEquivalentOf("max");
    }

    [Fact]
    public void Sha_is_40_lower_case_hex_letters()
    {
        ScriptRegistry.ShaHex(Script()).Should().MatchRegex("^[0-9a-f]{40}$");
        ScriptRegistry.ShaHex("return 1").Should().Be("e0e1f9fabfc9d4800c877a703b823ac0578ff8db");   // what SCRIPT LOAD answers
    }

    // ---------- error sent to the caller ----------

    [Fact]
    public void Below_minimum_error_carries_the_min_next_bid()
    {
        var ex = new DomainException(ErrorCodes.BelowMinimum, "Too low") { MinNextBidC = 1100 };

        var rpc = RpcErrors.From(ex, "corr-1");

        rpc.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        RpcErrors.TryGetDetail(rpc, out var detail).Should().BeTrue();
        detail!.MinNextBidC.Should().Be(1100);
    }
}
