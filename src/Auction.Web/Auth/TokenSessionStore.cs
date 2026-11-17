using System.Security.Cryptography;
using System.Text.Json;
using Auction.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace Auction.Web.Auth;

/// <summary>The user's Api tokens. Never sent to the browser.</summary>
public sealed record TokenSession(string AccessToken, DateTime AccessExpiresAt, string RefreshToken, DateTime RefreshExpiresAt)
{
    public static TokenSession From(AuthTokens tokens) => new(
        tokens.AccessToken,
        DateTimeOffset.FromUnixTimeMilliseconds(tokens.AccessExpiresMs).UtcDateTime,
        tokens.RefreshToken,
        DateTimeOffset.FromUnixTimeMilliseconds(tokens.RefreshExpiresMs).UtcDateTime);
}

public interface ITokenSessionStore
{
    Task<string> CreateAsync(TokenSession session, CancellationToken ct = default);
    Task<TokenSession?> GetAsync(string sessionId, CancellationToken ct = default);
    Task SaveAsync(string sessionId, TokenSession session, CancellationToken ct = default);
    Task RemoveAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Keeps tokens in Redis (encrypted with Data Protection), keyed by a random session id.
/// The auth cookie holds only that id, so all web copies see the same, newest tokens,
/// and a page reload after a token refresh does not reuse an old refresh token.
/// </summary>
public sealed class TokenSessionStore(IDistributedCache cache, IDataProtectionProvider dataProtection) : ITokenSessionStore
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("auction-web.token-session");

    private static string Key(string sessionId) => "session:" + sessionId;

    public async Task<string> CreateAsync(TokenSession session, CancellationToken ct = default)
    {
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await SaveAsync(sessionId, session, ct);
        return sessionId;
    }

    public async Task<TokenSession?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        var protectedBytes = await cache.GetAsync(Key(sessionId), ct);
        if (protectedBytes == null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TokenSession>(_protector.Unprotect(protectedBytes));
        }
        catch (CryptographicException)
        {
            return null;   // keys changed; the user signs in again
        }
    }

    public Task SaveAsync(string sessionId, TokenSession session, CancellationToken ct = default)
    {
        var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(session));
        var options = new DistributedCacheEntryOptions { AbsoluteExpiration = session.RefreshExpiresAt };
        return cache.SetAsync(Key(sessionId), bytes, options, ct);
    }

    public Task RemoveAsync(string sessionId, CancellationToken ct = default) =>
        cache.RemoveAsync(Key(sessionId), ct);
}
