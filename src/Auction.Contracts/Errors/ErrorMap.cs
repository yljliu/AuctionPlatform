using Auction.Domain.Errors;
using Grpc.Core;

namespace Auction.Contracts.Errors;

/// <summary>
/// Error code to gRPC status (design table 5.4).
/// Retryable = the same call may work later without changes.
/// </summary>
public static class ErrorMap
{
    private static readonly Dictionary<string, (StatusCode Status, bool Retryable)> Map = new()
    {
        [ErrorCodes.ValidationFailed] = (StatusCode.InvalidArgument, false),
        [ErrorCodes.Unauthenticated] = (StatusCode.Unauthenticated, false),
        [ErrorCodes.InvalidCredentials] = (StatusCode.Unauthenticated, false),
        [ErrorCodes.TotpRequired] = (StatusCode.Unauthenticated, false),
        [ErrorCodes.TotpInvalid] = (StatusCode.Unauthenticated, false),
        [ErrorCodes.RefreshReused] = (StatusCode.Unauthenticated, false),
        [ErrorCodes.WebhookSignatureInvalid] = (StatusCode.Unauthenticated, false),
        [ErrorCodes.AccountLocked] = (StatusCode.PermissionDenied, false),
        [ErrorCodes.EmailNotVerified] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.AccountSuspended] = (StatusCode.PermissionDenied, false),
        [ErrorCodes.AccountBanned] = (StatusCode.PermissionDenied, false),
        [ErrorCodes.RoleRequired] = (StatusCode.PermissionDenied, false),
        [ErrorCodes.NotOwner] = (StatusCode.PermissionDenied, false),
        [ErrorCodes.SellerNotVerified] = (StatusCode.PermissionDenied, false),
        [ErrorCodes.OwnLot] = (StatusCode.PermissionDenied, false),
        [ErrorCodes.NotFound] = (StatusCode.NotFound, false),
        [ErrorCodes.UsernameTaken] = (StatusCode.AlreadyExists, false),
        [ErrorCodes.SlugTaken] = (StatusCode.AlreadyExists, false),
        [ErrorCodes.AlreadyApplied] = (StatusCode.AlreadyExists, false),
        [ErrorCodes.AlreadyRated] = (StatusCode.AlreadyExists, false),
        [ErrorCodes.TokenInvalid] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.BelowMinimum] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.NotLive] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.Ended] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.AuctionFrozen] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.InsufficientFunds] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.EditLocked] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.InvalidState] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.DeadlinePassed] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.BuyNowUnavailable] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.ImageRequired] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.UploadInvalid] = (StatusCode.InvalidArgument, false),
        [ErrorCodes.PaymentDeclined] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.InstallmentsNotAllowed] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.DemoModeOnly] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.RunAlreadyActive] = (StatusCode.FailedPrecondition, false),
        [ErrorCodes.IdempotencyMismatch] = (StatusCode.Aborted, false),
        [ErrorCodes.VersionConflict] = (StatusCode.Aborted, false),
        [ErrorCodes.RateLimited] = (StatusCode.ResourceExhausted, true),
        [ErrorCodes.ServiceUnavailable] = (StatusCode.Unavailable, true),
        [ErrorCodes.Timeout] = (StatusCode.DeadlineExceeded, true),
        [ErrorCodes.Internal] = (StatusCode.Internal, false),
    };

    public static IReadOnlyCollection<string> KnownCodes => Map.Keys;

    /// <summary>Unknown codes map to INTERNAL.</summary>
    public static (StatusCode Status, bool Retryable) For(string code) =>
        Map.TryGetValue(code, out var entry) ? entry : (StatusCode.Internal, false);
}
