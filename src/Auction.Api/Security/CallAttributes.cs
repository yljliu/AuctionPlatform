using Auction.Domain.Enums;

namespace Auction.Api.Security;

/// <summary>The caller must send a valid access token.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SignedInAttribute : Attribute;

/// <summary>The caller must be signed in and have at least one of these roles.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireRoleAttribute(params Role[] roles) : Attribute
{
    public IReadOnlyList<Role> Roles { get; } = roles;
}

public enum RateLimitScope
{
    ClientIp,
    User,
}

/// <summary>Apply a rate limit policy from <see cref="Infrastructure.Redis.RateLimitPolicies"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RateLimitAttribute(string policy, RateLimitScope scope) : Attribute
{
    public string Policy { get; } = policy;
    public RateLimitScope Scope { get; } = scope;
}
