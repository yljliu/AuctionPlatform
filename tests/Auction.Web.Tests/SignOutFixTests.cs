using Auction.Web.Auth;
using Auction.Web.Services.Notify;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auction.Web.Tests;

/// <summary>Sign-out bug: the same-site check that replaced the empty antiforgery token, and the bell's sync Dispose.</summary>
public class SignOutFixTests
{
    private static HttpRequest Request(string host, string? origin = null, string? referer = null, string? forwardedHost = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString(host);
        if (origin != null)
        {
            http.Request.Headers.Origin = origin;
        }

        if (referer != null)
        {
            http.Request.Headers.Referer = referer;
        }

        if (forwardedHost != null)
        {
            http.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        }

        return http.Request;
    }

    [Theory]
    [InlineData("localhost:5001", "http://localhost:5001", null, null, true)]
    [InlineData("localhost:5001", "http://evil.example", null, null, false)]
    [InlineData("localhost:5001", "http://localhost:5002", null, null, false)]
    [InlineData("localhost:5001", null, "http://localhost:5001/a/1", null, true)]
    [InlineData("localhost:5001", "null", "http://localhost:5001/me", null, true)]
    [InlineData("localhost:5001", null, null, null, false)]
    [InlineData("web-1:8080", "http://localhost:5000", null, "localhost:5000", true)]
    [InlineData("web-1:8080", "http://localhost:5000", null, null, false)]
    [InlineData("example.com", "https://example.com", null, null, true)]
    public void Sign_out_only_from_this_site(string host, string? origin, string? referer, string? forwarded, bool ok) =>
        AccountEndpoints.IsSameOrigin(Request(host, origin, referer, forwarded)).Should().Be(ok);

    private sealed class NoSource : IUserFeedSource
    {
        public Task<Auction.Contracts.ListNotificationsResponse> LatestAsync(int count, CancellationToken ct) =>
            Task.FromResult(new Auction.Contracts.ListNotificationsResponse());

        public async IAsyncEnumerable<Auction.Contracts.UserEvent> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }

        public Task<int> MarkReadAsync(long id, CancellationToken ct) => Task.FromResult(0);

        public Task<int> MarkAllReadAsync(CancellationToken ct) => Task.FromResult(0);
    }

    [Fact]
    public async Task A_scope_disposed_the_sync_way_does_not_throw()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUserFeedSource, NoSource>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddScoped<UserFeed>();
        using var provider = services.BuildServiceProvider();

        var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<UserFeed>().Start();
        var dispose = () => scope.Dispose();   // what the error page does

        dispose.Should().NotThrow();
        await Task.Delay(50);
    }

    [Fact]
    public async Task Dispose_then_DisposeAsync_is_harmless()
    {
        var feed = new UserFeed(new NoSource(), NullLogger<UserFeed>.Instance);
        feed.Start();

        feed.Dispose();
        await feed.DisposeAsync();
        feed.Dispose();
    }
}
