using Auction.Infrastructure.Lifecycle;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>
/// Spec 05 task 1: LeaseManager rules, with an in-memory lease row and a hand-moved clock.
/// The real SQL statement is tested in Auction.Integration.Tests/LeaseManagerTests.
/// </summary>
public class LeaseUnitTests
{
    private readonly ManualClock _clock = new();
    private readonly FakeLeaseStore _store;

    public LeaseUnitTests() => _store = new FakeLeaseStore(_clock);

    private LeaseManager Manager(string holder) => new(_store, new LeaseOptions { Holder = holder }, _clock, NullLogger<LeaseManager>.Instance);

    [Fact]
    public async Task Two_managers_give_one_leader()
    {
        var a = Manager("a");
        var b = Manager("b");

        (await a.RenewOnceAsync()).Should().BeTrue();
        (await b.RenewOnceAsync()).Should().BeFalse();

        a.IsLeader.Should().BeTrue();
        a.FencingToken.Should().Be(1);
        b.IsLeader.Should().BeFalse();
        b.FencingToken.Should().Be(0);
    }

    [Fact]
    public async Task Renewing_keeps_the_same_token()
    {
        var a = Manager("a");
        await a.RenewOnceAsync();
        _clock.Advance(TimeSpan.FromSeconds(5));

        (await a.RenewOnceAsync()).Should().BeTrue();

        a.FencingToken.Should().Be(1);
    }

    [Fact]
    public async Task When_the_leader_stops_renewing_the_other_takes_over_with_token_plus_one()
    {
        var a = Manager("a");
        var b = Manager("b");
        await a.RenewOnceAsync();

        _clock.Advance(TimeSpan.FromSeconds(16));   // past the 15 s TTL, a never renewed

        a.IsLeader.Should().BeFalse("a stops by itself once its lease could have run out");
        (await b.RenewOnceAsync()).Should().BeTrue();
        b.FencingToken.Should().Be(2);
        (await a.RenewOnceAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Leader_stops_one_second_before_the_lease_can_expire()
    {
        var a = Manager("a");
        await a.RenewOnceAsync();

        _clock.Advance(TimeSpan.FromSeconds(13.9));
        a.IsLeader.Should().BeTrue();

        _clock.Advance(TimeSpan.FromSeconds(0.2));   // 14.1 s: inside the 1 s safety margin
        a.IsLeader.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_renew_stops_leadership_at_once()
    {
        var a = Manager("a");
        await a.RenewOnceAsync();

        _store.FailNext = true;
        (await a.RenewOnceAsync()).Should().BeFalse();

        a.IsLeader.Should().BeFalse();
        a.Current.Should().BeNull();
    }

    [Fact]
    public async Task Release_lets_another_copy_take_over_at_once()
    {
        var a = Manager("a");
        var b = Manager("b");
        await a.RenewOnceAsync();

        await a.ReleaseAsync();

        a.IsLeader.Should().BeFalse();
        (await b.RenewOnceAsync()).Should().BeTrue();
        b.FencingToken.Should().Be(2);
    }

    [Fact]
    public async Task Current_grant_names_the_holder_and_token()
    {
        var a = Manager("a");
        await a.RenewOnceAsync();

        a.Current.Should().Be(new LeaseGrant("lifecycle", "a", 1));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Same rules as the SQL statement: renew if held, take if expired, new holder gets token + 1.</summary>
    private sealed class FakeLeaseStore(TimeProvider clock) : ILeaseStore
    {
        private string _holder = "";
        private long _token;
        private DateTimeOffset _expires = DateTimeOffset.MinValue;

        public bool FailNext { get; set; }

        public Task<long?> TryAcquireAsync(string leaseName, string holder, TimeSpan ttl, CancellationToken ct = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new TimeoutException("SQL did not answer");
            }

            var now = clock.GetUtcNow();
            if (_holder != holder && _expires >= now)
            {
                return Task.FromResult<long?>(null);
            }

            if (_holder != holder)
            {
                _token++;
            }

            _holder = holder;
            _expires = now + ttl;
            return Task.FromResult<long?>(_token);
        }

        public Task ReleaseAsync(string leaseName, string holder, CancellationToken ct = default)
        {
            if (_holder == holder)
            {
                _expires = DateTimeOffset.MinValue;
            }

            return Task.CompletedTask;
        }
    }
}
