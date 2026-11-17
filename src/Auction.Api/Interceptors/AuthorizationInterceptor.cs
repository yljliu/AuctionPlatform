using Auction.Api.Security;
using Auction.Domain.Errors;
using Auction.Infrastructure.Identity;
using Grpc.Core;

namespace Auction.Api.Interceptors;

/// <summary>
/// Checks [SignedIn] and [RequireRole] (R5.1, R5.2). JwtBearer has already read the token.
/// Done here, not with [Authorize], so the caller gets our ErrorDetail codes.
/// </summary>
public sealed class AuthorizationInterceptor : GuardInterceptor
{
    protected override Task CheckAsync(ServerCallContext context)
    {
        var needsRole = context.GetEndpointMetadata<RequireRoleAttribute>();
        var needsSignIn = needsRole != null || context.GetEndpointMetadata<SignedInAttribute>() != null;
        if (!needsSignIn)
        {
            return Task.CompletedTask;
        }

        var user = context.GetHttpContext().User;
        if (user.Identity?.IsAuthenticated != true)
        {
            throw new DomainException(ErrorCodes.Unauthenticated, "Please sign in.");
        }

        if (needsRole != null)
        {
            var hasRole = false;
            foreach (var role in needsRole.Roles)
            {
                if (user.HasClaim(AuthClaims.Role, role.ToString()))
                {
                    hasRole = true;
                    break;
                }
            }

            if (!hasRole)
            {
                throw new DomainException(ErrorCodes.RoleRequired, "You do not have permission for this.");
            }
        }

        return Task.CompletedTask;
    }
}
