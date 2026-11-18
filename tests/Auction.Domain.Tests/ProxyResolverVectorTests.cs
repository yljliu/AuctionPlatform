using System.Text.Json;
using Auction.Domain.Bidding;
using Auction.Domain.Enums;
using Auction.Domain.Errors;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>
/// Spec 03 task 2: every case in tests/vectors/proxy.json must give exactly the expected result.
/// The same file checks bid.lua in task 4, so C# and Lua stay the same (R4.7).
/// </summary>
public class ProxyResolverVectorTests
{
    /// <summary>The increment table used when a case has no "inc": seed table 1.</summary>
    public const string Table1 = "0:100,5000:250,25000:500,100000:1000,500000:5000";

    // Short names in the file map to fixed ids.
    private static readonly Dictionary<string, Guid> Users = new()
    {
        ["A"] = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        ["B"] = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
        ["C"] = Guid.Parse("cccccccc-0000-0000-0000-000000000003"),
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public sealed record VectorState(long PriceC, string? Leader, long LeaderMaxC, int BidCount, long StartC);

    public sealed record VectorRow(string User, long AmountC, string Kind);

    public sealed record VectorExpect(
        string? Result, string? Error, long? MinNextC, long PriceC, string? Leader, long LeaderMaxC, int BidCount,
        string? Outbid, List<VectorRow>? Rows);

    public sealed record Vector(string Name, string? Inc, VectorState State, string User, long MaxC, VectorExpect Expect)
    {
        public override string ToString() => Name;   // shown as the test case name
    }

    public static IReadOnlyList<Vector> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors", "proxy.json");
        return JsonSerializer.Deserialize<List<Vector>>(File.ReadAllText(path), Json)
               ?? throw new InvalidOperationException("proxy.json is empty.");
    }

    public static TheoryData<Vector> Cases()
    {
        var data = new TheoryData<Vector>();
        foreach (var vector in Load())
        {
            data.Add(vector);
        }

        return data;
    }

    [Fact]
    public void File_has_at_least_25_cases_with_unique_names()
    {
        var vectors = Load();

        vectors.Should().HaveCountGreaterThanOrEqualTo(25);
        vectors.Select(v => v.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void File_covers_every_result_and_the_error()
    {
        var outcomes = Load().Select(v => v.Expect.Result ?? v.Expect.Error).Distinct();

        outcomes.Should().Contain(["Leading", "OutbidByProxy", "MaxRaised", "BELOW_MINIMUM"]);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_matches(Vector vector)
    {
        var increments = IncrementTable.Parse(vector.Inc ?? Table1);
        var state = new BidState(
            vector.State.PriceC,
            vector.State.Leader == null ? null : Users[vector.State.Leader],
            vector.State.LeaderMaxC,
            vector.State.BidCount,
            vector.State.StartC);
        var user = Users[vector.User];

        if (vector.Expect.Error != null)
        {
            var act = () => ProxyResolver.Resolve(state, user, vector.MaxC, increments);
            var ex = act.Should().Throw<DomainException>().Which;
            ex.Code.Should().Be(vector.Expect.Error);
            ex.MinNextBidC.Should().Be(vector.Expect.MinNextC);
            return;
        }

        var resolution = ProxyResolver.Resolve(state, user, vector.MaxC, increments);

        resolution.Result.Should().Be(Enum.Parse<BidResult>(vector.Expect.Result!));
        resolution.Next.PriceC.Should().Be(vector.Expect.PriceC);
        resolution.Next.Leader.Should().Be(Users[vector.Expect.Leader!]);
        resolution.Next.LeaderMaxC.Should().Be(vector.Expect.LeaderMaxC);
        resolution.Next.BidCount.Should().Be(vector.Expect.BidCount);
        resolution.Next.StartC.Should().Be(state.StartC);
        resolution.Outbid.Should().Be(vector.Expect.Outbid == null ? null : Users[vector.Expect.Outbid]);

        var expectedRows = vector.Expect.Rows!.Select(r => new BidRow(Users[r.User], r.AmountC, Enum.Parse<BidKind>(r.Kind)));
        resolution.Rows.Should().Equal(expectedRows);
    }

    // ---------- bad state is refused ----------

    private static readonly IncrementTable Increments = IncrementTable.Parse(Table1);

    [Fact]
    public void Leader_without_bids_is_refused()
    {
        var act = () => ProxyResolver.Resolve(new BidState(0, Users["A"], 5000, 0, 1000), Users["B"], 5000, Increments);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Bids_without_a_leader_are_refused()
    {
        var act = () => ProxyResolver.Resolve(new BidState(1000, null, 5000, 1, 1000), Users["B"], 5000, Increments);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Leader_max_below_price_is_refused()
    {
        var act = () => ProxyResolver.Resolve(new BidState(2000, Users["A"], 1000, 1, 1000), Users["B"], 5000, Increments);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Zero_start_is_refused()
    {
        var act = () => ProxyResolver.Resolve(new BidState(0, null, 0, 0, 0), Users["A"], 5000, Increments);
        act.Should().Throw<ArgumentException>();
    }
}
