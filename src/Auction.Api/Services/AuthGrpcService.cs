using Auction.Api.Security;
using Auction.Contracts;
using Auction.Infrastructure.Identity;
using Auction.Infrastructure.Persistence.Entities;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Auction.Api.Services;

public sealed class AuthGrpcService(AuthManager auth, AppealManager appeals, SellerApplicationManager sellers, Infrastructure.Persistence.AuctionDbContext db) : AuthService.AuthServiceBase
{
    [SignedIn]
    public override async Task<SellerApplication> ApplyForSeller(ApplyForSellerRequest request, ServerCallContext context) =>
        ToProto(await sellers.ApplyAsync(context.GetUserId(), request.DisplayName, context.CancellationToken));

    [SignedIn]
    public override async Task<SellerApplication> GetSellerApplication(Empty request, ServerCallContext context) =>
        ToProto(await sellers.GetAsync(context.GetUserId(), context.CancellationToken));

    public static SellerApplication ToProto(SellerApplicationView v) => new()
    {
        UserId = v.UserId.ToString(),
        Username = v.Username,
        Email = v.Email,
        Status = v.Status?.ToString() ?? "None",
        DisplayName = v.DisplayName ?? "",
        RejectReason = v.RejectReason ?? "",
        IsSeller = v.IsSeller,
    };

    [RateLimit("login", RateLimitScope.ClientIp)]
    public override async Task<Contracts.AppealStatus> SubmitAppeal(SubmitAppealRequest request, ServerCallContext context) =>
        ToProto(await appeals.SubmitAsync(request.Email, request.Password, request.Message, context.CancellationToken));

    [RateLimit("login", RateLimitScope.ClientIp)]
    public override async Task<Contracts.AppealStatus> GetAppealStatus(AppealLoginRequest request, ServerCallContext context) =>
        ToProto(await appeals.GetStatusAsync(request.Email, request.Password, context.CancellationToken));

    private static Contracts.AppealStatus ToProto(AppealStatusView v) => new()
    {
        UserStatus = v.UserStatus.ToString(),
        ActiveStrikes = v.ActiveStrikes,
        AppealStatus_ = v.Latest?.Status.ToString() ?? "None",
        AdminNote = v.Latest?.AdminNote ?? "",
        CreatedAtMs = v.Latest == null ? 0 : ToUnixMs(v.Latest.CreatedAt),
        ResolvedAtMs = v.Latest?.ResolvedAt == null ? 0 : ToUnixMs(v.Latest.ResolvedAt.Value),
    };

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
        return new RefreshTokenResponse { Tokens = ToProto(result.Tokens), Me = ToMe(result.User) };
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
        var me = ToMe(user);
        me.ActiveStrikes = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
            db.UserStrikes, s => s.UserId == user.UserId && s.ClearedAt == null, context.CancellationToken);
        return me;
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
