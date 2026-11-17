using Auction.Contracts;
using Auction.Contracts.Errors;
using Grpc.Core;
using Microsoft.AspNetCore.Components.Authorization;

namespace Auction.Web.Auth;

/// <summary>
/// One per Blazor circuit (scoped). Hands out a valid access token and refreshes it
/// when it is about to run out.
/// </summary>
public sealed class TokenStore(
    AuthenticationStateProvider authState,
    ITokenSessionStore sessions,
    IServiceProvider services,
    TimeProvider clock,
    ILogger<TokenStore> log)
{
    /// <summary>Cookie claim that holds the session id.</summary>
    public const string SessionClaim = "sid";

    private static readonly TimeSpan RefreshEarly = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _sessionId;
    private TokenSession? _session;
    private bool _loaded;

    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loaded)
            {
                var state = await authState.GetAuthenticationStateAsync();
                _sessionId = state.User.FindFirst(SessionClaim)?.Value;
                if (_sessionId != null)
                {
                    _session = await sessions.GetAsync(_sessionId, ct);
                }

                _loaded = true;
            }

            if (_sessionId == null || _session == null)
            {
                return null;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            if (!forceRefresh && _session.AccessExpiresAt > now + RefreshEarly)
            {
                return _session.AccessToken;
            }

            // Another tab or web copy may have refreshed already.
            var latest = await sessions.GetAsync(_sessionId, ct);
            if (latest == null)
            {
                _session = null;
                return null;
            }

            if (latest.RefreshToken != _session.RefreshToken && latest.AccessExpiresAt > now + RefreshEarly)
            {
                _session = latest;
                return latest.AccessToken;
            }

            // Resolved here, not in the constructor, because the client's interceptor needs this TokenStore.
            var auth = services.GetRequiredService<AuthService.AuthServiceClient>();
            try
            {
                var reply = await auth.RefreshTokenAsync(new RefreshTokenRequest { RefreshToken = latest.RefreshToken }, cancellationToken: ct);
                _session = TokenSession.From(reply.Tokens);
                await sessions.SaveAsync(_sessionId, _session, ct);
                return _session.AccessToken;
            }
            catch (RpcException ex)
            {
                log.LogInformation("Token refresh failed with {Code}; user must sign in again", RpcErrors.CodeOf(ex));
                await sessions.RemoveAsync(_sessionId, ct);
                _session = null;
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
