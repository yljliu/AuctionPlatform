namespace Auction.Domain.Errors;

/// <summary>Every error code a caller can get back (design section 5.4).</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string TotpRequired = "TOTP_REQUIRED";
    public const string TotpInvalid = "TOTP_INVALID";
    public const string RefreshReused = "REFRESH_REUSED";
    public const string WebhookSignatureInvalid = "WEBHOOK_SIGNATURE_INVALID";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string EmailNotVerified = "EMAIL_NOT_VERIFIED";
    public const string AccountSuspended = "ACCOUNT_SUSPENDED";
    public const string AccountBanned = "ACCOUNT_BANNED";
    public const string RoleRequired = "ROLE_REQUIRED";
    public const string NotOwner = "NOT_OWNER";
    public const string SellerNotVerified = "SELLER_NOT_VERIFIED";
    public const string OwnLot = "OWN_LOT";
    public const string NotFound = "NOT_FOUND";
    public const string UsernameTaken = "USERNAME_TAKEN";
    public const string SlugTaken = "SLUG_TAKEN";
    public const string AlreadyApplied = "ALREADY_APPLIED";
    public const string AlreadyRated = "ALREADY_RATED";
    public const string TokenInvalid = "TOKEN_INVALID";
    public const string BelowMinimum = "BELOW_MINIMUM";
    public const string NotLive = "NOT_LIVE";
    public const string Ended = "ENDED";
    public const string AuctionFrozen = "AUCTION_FROZEN";
    public const string InsufficientFunds = "INSUFFICIENT_FUNDS";
    public const string EditLocked = "EDIT_LOCKED";
    public const string InvalidState = "INVALID_STATE";
    public const string DeadlinePassed = "DEADLINE_PASSED";
    public const string BuyNowUnavailable = "BUY_NOW_UNAVAILABLE";
    public const string ImageRequired = "IMAGE_REQUIRED";
    public const string UploadInvalid = "UPLOAD_INVALID";
    public const string PaymentDeclined = "PAYMENT_DECLINED";
    public const string InstallmentsNotAllowed = "INSTALLMENTS_NOT_ALLOWED";
    public const string DemoModeOnly = "DEMO_MODE_ONLY";
    public const string RunAlreadyActive = "RUN_ALREADY_ACTIVE";
    public const string IdempotencyMismatch = "IDEMPOTENCY_MISMATCH";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string RateLimited = "RATE_LIMITED";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string Timeout = "TIMEOUT";
    public const string Internal = "INTERNAL";
}
