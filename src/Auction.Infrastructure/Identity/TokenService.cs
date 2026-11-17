using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Auction.Domain.Enums;
using Auction.Infrastructure.Persistence.Entities;
using Microsoft.IdentityModel.Tokens;

namespace Auction.Infrastructure.Identity;

/// <summary>Bound from config section "JWT" (env JWT__SigningKey and so on).</summary>
public sealed class JwtOptions
{
    public string SigningKey { get; set; } = "";
    public string Issuer { get; set; } = "auction-api";
    public string Audience { get; set; } = "auction";
    public int AccessTokenMinutes { get; set; } = 10;
    public int RefreshTokenDays { get; set; } = 14;
}

/// <summary>Claim names inside the access token.</summary>
public static class AuthClaims
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Role = "role";
    public const string StatusVersion = "sv";
}

public sealed record TokenPair(string AccessToken, DateTime AccessExpiresAt, string RefreshToken, DateTime RefreshExpiresAt);

/// <summary>Makes access tokens (JWT, HS256) and refresh tokens (random, only the hash is stored).</summary>
public sealed class TokenService
{
    private readonly JwtOptions _options;
    private readonly TimeProvider _clock;

    public TokenService(JwtOptions options, TimeProvider clock)
    {
        if (Encoding.UTF8.GetByteCount(options.SigningKey ?? "") < 32)
        {
            throw new InvalidOperationException("JWT__SigningKey must be at least 32 bytes long.");
        }

        _options = options;
        _clock = clock;
        SigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey!));
    }

    public SymmetricSecurityKey SigningKey { get; }

    public JwtOptions Options => _options;

    public (string Token, DateTime ExpiresAt) CreateAccessToken(User user, IEnumerable<Role> roles)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(AuthClaims.Subject, user.UserId.ToString()),
            new(AuthClaims.Name, user.Username),
            new(AuthClaims.StatusVersion, user.StatusVersion.ToString(CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(AuthClaims.Role, role.ToString()));
        }

        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }

    public (string Token, byte[] Hash, DateTime ExpiresAt) CreateRefreshToken()
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var expires = _clock.GetUtcNow().UtcDateTime.AddDays(_options.RefreshTokenDays);
        return (token, HashRefreshToken(token), expires);
    }

    public static byte[] HashRefreshToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
