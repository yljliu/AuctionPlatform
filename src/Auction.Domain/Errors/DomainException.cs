namespace Auction.Domain.Errors;

/// <summary>One bad input field: which field, which rule it broke, and a message for the user.</summary>
public sealed record FieldIssue(string Field, string Rule, string Message);

/// <summary>
/// A known business error. The Api turns it into a gRPC status plus an ErrorDetail.
/// Code is one of <see cref="ErrorCodes"/>.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string code, string message, IReadOnlyList<FieldIssue>? fields = null)
        : base(message)
    {
        Code = code;
        Fields = fields ?? [];
    }

    public string Code { get; }

    public IReadOnlyList<FieldIssue> Fields { get; }

    /// <summary>For RATE_LIMITED and ACCOUNT_LOCKED: how long to wait, in milliseconds.</summary>
    public long RetryAfterMs { get; init; }

    /// <summary>For BELOW_MINIMUM: the smallest bid that would be accepted, in cents.</summary>
    public long? MinNextBidC { get; init; }
}
