using Auction.Api.Security;
using Auction.Domain.Errors;
using Auction.Infrastructure.Redis;
using Grpc.Core;

namespace Auction.Api.Interceptors;

/// <summary>Applies [RateLimit] policies (R7). Over the limit: RATE_LIMITED with retry_after_ms.</summary>
public sealed class RateLimitInterceptor(IRateLimiter limiter) : GuardInterceptor
{
    private static readonly Dictionary<string, RateLimitPolicy> Policies = new()
    {
        [RateLimitPolicies.Login.Name] = RateLimitPolicies.Login,
        [RateLimitPolicies.Bid.Name] = RateLimitPolicies.Bid,
    };

    protected override async Task CheckAsync(ServerCallContext context)
    {
        var attribute = context.GetEndpointMetadata<RateLimitAttribute>();
        if (attribute == null)
        {
            return;
        }

        var policy = Policies[attribute.Policy];
        var id = attribute.Scope == RateLimitScope.User
            ? context.GetUserId().ToString()
            : context.GetClientIp();

        var result = await limiter.TryAcquireAsync(policy, id);
        if (!result.Allowed)
        {
            throw new DomainException(ErrorCodes.RateLimited, "Too many requests. Please wait and try again.")
            {
                RetryAfterMs = result.RetryAfterMs,
            };
        }
    }
}
