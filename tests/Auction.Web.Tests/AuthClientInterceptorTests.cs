using System.Security.Claims;
using Auction.Web.Auth;
using FluentAssertions;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auction.Web.Tests;

/// <summary>Every kind of call carries the access token, including client streams like UploadImage (photo upload bug).</summary>
public class AuthClientInterceptorTests
{
    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(TokenStore.SessionClaim, "sid1")], "test"))));
    }

    private sealed class OneSession : ITokenSessionStore
    {
        private readonly TokenSession _session = new("access-1", DateTime.UtcNow.AddHours(1), "refresh-1", DateTime.UtcNow.AddDays(1));

        public Task<string> CreateAsync(TokenSession session, CancellationToken ct = default) => Task.FromResult("sid1");

        public Task<TokenSession?> GetAsync(string sessionId, CancellationToken ct = default) => Task.FromResult<TokenSession?>(_session);

        public Task SaveAsync(string sessionId, TokenSession session, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static AuthClientInterceptor Interceptor() =>
        new(new TokenStore(new SignedIn(), new OneSession(), new ServiceCollection().BuildServiceProvider(), TimeProvider.System,
            NullLogger<TokenStore>.Instance));

    private static readonly Method<string, string> Upload = new(MethodType.ClientStreaming, "auction.v1.SellerService", "UploadImage",
        Marshallers.StringMarshaller, Marshallers.StringMarshaller);

    [Fact]
    public void A_client_stream_gets_the_bearer_token()
    {
        Metadata? sent = null;
        var context = new ClientInterceptorContext<string, string>(Upload, null, new CallOptions(new Metadata { { "x-client-ip", "1.2.3.4" } }));

        Interceptor().AsyncClientStreamingCall(context, c =>
        {
            sent = c.Options.Headers;
            return null!;
        });

        sent.Should().NotBeNull();
        sent!.GetValue("authorization").Should().Be("Bearer access-1");
        sent.GetValue("x-client-ip").Should().Be("1.2.3.4", "other headers are kept");
    }
}
