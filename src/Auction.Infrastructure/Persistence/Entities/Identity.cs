using Auction.Domain.Enums;

namespace Auction.Infrastructure.Persistence.Entities;

public class User
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string EmailNormalized { get; private set; } = "";   // computed in SQL
    public DateTime? EmailVerifiedAt { get; set; }
    public string PasswordHash { get; set; } = "";
    public string? PhoneNumber { get; set; }
    public byte[]? TotpSecretEnc { get; set; }
    public UserStatus Status { get; set; }
    public DateTime? SuspendedUntil { get; set; }
    public int StatusVersion { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? FirstFailedLoginAt { get; set; }   // start of the 10 minute failure window
    public DateTime? LockedUntil { get; set; }
    public bool IsBot { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<UserRole> Roles { get; set; } = [];
}

public class UserRole
{
    public Guid UserId { get; set; }
    public Role Role { get; set; }
    public DateTime GrantedAt { get; set; }
    public Guid? GrantedBy { get; set; }
}

public class RefreshToken
{
    public Guid TokenId { get; set; }
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? UserAgent { get; set; }
    public string? Ip { get; set; }
}

public class SellerProfile
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public SellerStatus Status { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public Guid? VerifiedBy { get; set; }
    public string? RejectReason { get; set; }
    public int RatingCount { get; set; }
    public int RatingSum { get; set; }
    public int CompletedSales { get; set; }
}
