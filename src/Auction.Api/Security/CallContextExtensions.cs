using Auction.Domain.Errors;
using Auction.Infrastructure.Identity;
using Grpc.Core;

namespace Auction.Api.Security;

public static class CallContextExtensions
{
    public const string CorrelationItem = "correlation_id";

    /// <summary>
    /// Web servers pass the browser's IP in this header, so the login limit is per user IP,
    /// not per web server. The Api port must stay on the internal network.
    /// </summary>
    public const string ClientIpHeader = "x-client-ip";

    public static string? GetCorrelationId(this ServerCallContext context) =>
        context.GetHttpContext().Items[CorrelationItem] as string;

    public static Guid GetUserId(this ServerCallContext context)
    {
        var sub = context.GetHttpContext().User.FindFirst(AuthClaims.Subject)?.Value;
        return Guid.TryParse(sub, out var id)
            ? id
            : throw new DomainException(ErrorCodes.Unauthenticated, "Please sign in.");
    }

    public static string GetClientIp(this ServerCallContext context)
    {
        var forwarded = context.RequestHeaders.GetValue(ClientIpHeader);
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            return forwarded.Length <= 45 ? forwarded : forwarded[..45];
        }

        return context.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static string? GetUserAgent(this ServerCallContext context) =>
        context.GetHttpContext().Request.Headers.UserAgent.ToString();

    /// <summary>The caller's id on public calls, or null when signed out.</summary>
    public static Guid? GetOptionalUserId(this ServerCallContext context)
    {
        var user = context.GetHttpContext().User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return Guid.TryParse(user.FindFirst(AuthClaims.Subject)?.Value, out var id) ? id : null;
    }

    public static bool IsAdmin(this ServerCallContext context) =>
        context.GetHttpContext().User.HasClaim(AuthClaims.Role, nameof(Domain.Enums.Role.Admin));

    public static T? GetEndpointMetadata<T>(this ServerCallContext context) where T : class =>
        context.GetHttpContext().GetEndpoint()?.Metadata.GetMetadata<T>();
}
