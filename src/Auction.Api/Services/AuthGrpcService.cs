using Auction.Api.Security;
using Auction.Contracts;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Persistence.Entities;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Auction.Api.Services;

public sealed class AuthGrpcService(AuthManager auth) : AuthService.AuthServiceBase
{
    public override async Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        var userId = await auth.RegisterAsync(request.Username, request.Email, request.Password, context.CancellationToken);
        return new RegisterResponse { UserId = userId.ToString() };
    }

    [RateLimit("login", RateLimitScope.ClientIp)]
    public override async Task<LoginResponse> Login(LoginRequest request, ServerCallContext context)
    {
        var result = await auth.LoginAsync(request.Email, request.Password, context.GetUserAgent(), context.GetClientIp(), context.CancellationToken);
        return new LoginResponse { Tokens = ToProto(result.Tokens), Me = ToMe(result.User) };
    }

    public override async Task<RefreshTokenResponse> RefreshToken(RefreshTokenRequest request, ServerCallContext context)
    {
        var result = await auth.RefreshAsync(request.RefreshToken, context.GetUserAgent(), context.GetClientIp(), context.CancellationToken);
        return new RefreshTokenResponse { Tokens = ToProto(result.Tokens) };
    }

    public override async Task<Empty> Logout(LogoutRequest request, ServerCallContext context)
    {
        await auth.LogoutAsync(request.RefreshToken, context.CancellationToken);
        return new Empty();
    }

    [SignedIn]
    public override async Task<Me> GetMe(Empty request, ServerCallContext context)
    {
        var user = await auth.GetUserAsync(context.GetUserId(), context.CancellationToken);
        return ToMe(user);
    }

    public static AuthTokens ToProto(TokenPair pair) => new()
    {
        AccessToken = pair.AccessToken,
        AccessExpiresMs = ToUnixMs(pair.AccessExpiresAt),
        RefreshToken = pair.RefreshToken,
        RefreshExpiresMs = ToUnixMs(pair.RefreshExpiresAt),
    };

    private static Me ToMe(User user)
    {
        var me = new Me
        {
            UserId = user.UserId.ToString(),
            Username = user.Username,
            Email = user.Email,
            Status = user.Status.ToString(),
            StatusVersion = user.StatusVersion,
        };
        foreach (var role in user.Roles)
        {
            me.Roles.Add(role.Role.ToString());
        }

        return me;
    }

    private static long ToUnixMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
