using Auction.Domain.Enums;
using Auction.Domain.Errors;
using Auction.Domain.Identity;
using Auction.Infrastructure.Persistence;
using Auction.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Auction.Infrastructure.Identity;

public sealed record AuthResult(User User, TokenPair Tokens);

/// <summary>Register, login, refresh, logout (requirements R3 and R4).</summary>
public sealed class AuthManager(AuctionDbContext db, TokenService tokens, TimeProvider clock)
{
    public const int BcryptCost = 12;
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(10);

    // Used when the email is unknown, so a wrong email takes as long as a wrong password.
    private static readonly Lazy<string> DummyHash = new(() => BCrypt.Net.BCrypt.HashPassword("timing-dummy-password", BcryptCost));

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---------- register ----------

    /// <summary>
    /// Returns the new user id. If the email is already used, returns a random id and creates nothing,
    /// so a caller cannot find out which emails have accounts.
    /// </summary>
    public async Task<Guid> RegisterAsync(string? username, string? email, string? password, CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        email = (email ?? "").Trim();
        password ??= "";

        var issues = RegistrationRules.Validate(username, email, password);
        if (issues.Count > 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Some fields are not valid.", issues);
        }

        // Hash first so every path takes about the same time.
        var hash = BCrypt.Net.BCrypt.HashPassword(password, BcryptCost);

        if (await UsernameTakenAsync(username, ct))
        {
            throw UsernameTaken();
        }

        if (await EmailTakenAsync(email, ct))
        {
            return Guid.NewGuid();
        }

        var now = Now;
        var user = new User
        {
            UserId = Guid.NewGuid(),
            Username = username,
            Email = email,
            PasswordHash = hash,
            Status = UserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.Roles.Add(new UserRole { UserId = user.UserId, Role = Role.Bidder, GrantedAt = now });
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another register with the same name or email won the race.
            db.ChangeTracker.Clear();
            if (await UsernameTakenAsync(username, ct))
            {
                throw UsernameTaken();
            }

            if (await EmailTakenAsync(email, ct))
            {
                return Guid.NewGuid();
            }

            throw;
        }

        return user.UserId;
    }

    // ---------- login ----------

    public async Task<AuthResult> LoginAsync(string? email, string? password, string? userAgent, string? ip, CancellationToken ct = default)
    {
        var normalized = (email ?? "").Trim().ToUpperInvariant();
        password ??= "";

        var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.EmailNormalized == normalized, ct);
        if (user == null)
        {
            BCrypt.Net.BCrypt.Verify(password, DummyHash.Value);
            throw InvalidCredentials();
        }

        var now = Now;
        if (user.LockedUntil > now)
        {
            throw Locked(user.LockedUntil.Value, now);
        }

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            await RecordFailedLoginAsync(user, now, ct);
            throw InvalidCredentials();
        }

        EnsureMayUseAccount(user, now);

        user.FailedLogins = 0;
        user.FirstFailedLoginAt = null;
        user.LockedUntil = null;
        user.UpdatedAt = now;

        var (pair, _) = IssueTokens(user, Guid.NewGuid(), userAgent, ip, now);
        await db.SaveChangesAsync(ct);
        return new AuthResult(user, pair);
    }

    /// <summary>
    /// Signs in bot users without a password (spec 08: AdminService.CreateBots). Only for IsBot users, so it can
    /// never open a person's account. The tokens are the same kind a login gives; bids still go through all checks.
    /// </summary>
    public async Task<IReadOnlyList<AuthResult>> IssueForBotsAsync(IReadOnlyCollection<Guid> botIds, string? ip, CancellationToken ct = default)
    {
        var bots = await db.Users.Include(u => u.Roles).Where(u => botIds.Contains(u.UserId) && u.IsBot).ToListAsync(ct);
        var now = Now;
        var results = new List<AuthResult>(bots.Count);
        foreach (var bot in bots)
        {
            EnsureMayUseAccount(bot, now);
            var (pair, _) = IssueTokens(bot, Guid.NewGuid(), "auction-simulator", ip, now);
            results.Add(new AuthResult(bot, pair));
        }

        await db.SaveChangesAsync(ct);
        return results;
    }

    private async Task RecordFailedLoginAsync(User user, DateTime now, CancellationToken ct)
    {
        if (user.FirstFailedLoginAt == null || now - user.FirstFailedLoginAt.Value > FailureWindow)
        {
            user.FirstFailedLoginAt = now;
            user.FailedLogins = 0;
        }

        user.FailedLogins++;

        if (user.FailedLogins >= MaxFailedLogins)
        {
            user.LockedUntil = now + LockDuration;
            user.FailedLogins = 0;
            user.FirstFailedLoginAt = null;
        }

        user.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another login attempt changed the row at the same time. This attempt still fails.
        }
    }

    // ---------- refresh and logout ----------

    public async Task<AuthResult> RefreshAsync(string? refreshToken, string? userAgent, string? ip, CancellationToken ct = default)
    {
        var hash = TokenService.HashRefreshToken(refreshToken ?? "");
        var row = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row == null)
        {
            throw new DomainException(ErrorCodes.Unauthenticated, "Please sign in again.");
        }

        var now = Now;

        if (row.RevokedAt != null)
        {
            await RevokeFamilyAsync(row.FamilyId, now, ct);
            throw RefreshReused();
        }

        if (row.ExpiresAt <= now)
        {
            throw new DomainException(ErrorCodes.Unauthenticated, "Your sign-in has expired. Please sign in again.");
        }

        // Claim the token. If two refreshes race, only one gets 1 row back.
        var claimed = await db.RefreshTokens
            .Where(t => t.TokenId == row.TokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, (DateTime?)now), ct);
        if (claimed == 0)
        {
            await RevokeFamilyAsync(row.FamilyId, now, ct);
            throw RefreshReused();
        }

        var user = await db.Users.Include(u => u.Roles).FirstAsync(u => u.UserId == row.UserId, ct);
        try
        {
            EnsureMayUseAccount(user, now);
        }
        catch (DomainException)
        {
            await RevokeFamilyAsync(row.FamilyId, now, ct);
            throw;
        }

        var (pair, newTokenId) = IssueTokens(user, row.FamilyId, userAgent, ip, now);
        await db.SaveChangesAsync(ct);

        await db.RefreshTokens
            .Where(t => t.TokenId == row.TokenId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReplacedBy, (Guid?)newTokenId), ct);

        return new AuthResult(user, pair);
    }

    /// <summary>Revokes this refresh token. Unknown or already revoked tokens are ignored.</summary>
    public async Task LogoutAsync(string? refreshToken, CancellationToken ct = default)
    {
        var hash = TokenService.HashRefreshToken(refreshToken ?? "");
        var now = Now;
        await db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, (DateTime?)now), ct);
    }

    public async Task<User> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        return await db.Users.AsNoTracking().Include(u => u.Roles).FirstOrDefaultAsync(u => u.UserId == userId, ct)
               ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");
    }

    // ---------- helpers ----------

    private (TokenPair Pair, Guid TokenId) IssueTokens(User user, Guid familyId, string? userAgent, string? ip, DateTime now)
    {
        var (access, accessExpires) = tokens.CreateAccessToken(user, user.Roles.Select(r => r.Role));
        var (refresh, refreshHash, refreshExpires) = tokens.CreateRefreshToken();
        var tokenId = Guid.NewGuid();

        db.RefreshTokens.Add(new RefreshToken
        {
            TokenId = tokenId,
            UserId = user.UserId,
            FamilyId = familyId,
            TokenHash = refreshHash,
            ExpiresAt = refreshExpires,
            CreatedAt = now,
            UserAgent = Cut(userAgent, 200),
            Ip = Cut(ip, 45),
        });

        return (new TokenPair(access, accessExpires, refresh, refreshExpires), tokenId);
    }

    private Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, (DateTime?)now), ct);

    private static void EnsureMayUseAccount(User user, DateTime now)
    {
        if (user.Status == UserStatus.Banned)
        {
            throw new DomainException(ErrorCodes.AccountBanned, "This account is banned.");
        }

        if (user.Status == UserStatus.Suspended && (user.SuspendedUntil == null || user.SuspendedUntil > now))
        {
            throw new DomainException(ErrorCodes.AccountSuspended, "This account is suspended.");
        }
    }

    private Task<bool> UsernameTakenAsync(string username, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Username == username, ct);

    private Task<bool> EmailTakenAsync(string email, CancellationToken ct)
    {
        var normalized = email.ToUpperInvariant();
        return db.Users.AnyAsync(u => u.EmailNormalized == normalized, ct);
    }

    private static string? Cut(string? value, int max) =>
        value == null ? null : value.Length <= max ? value : value[..max];

    private static DomainException InvalidCredentials() =>
        new(ErrorCodes.InvalidCredentials, "Wrong email or password.");

    private static DomainException UsernameTaken() =>
        new(ErrorCodes.UsernameTaken, "That username is already in use.");

    private static DomainException RefreshReused() =>
        new(ErrorCodes.RefreshReused, "This sign-in was used twice, so it was closed. Please sign in again.");

    private static DomainException Locked(DateTime until, DateTime now) =>
        new(ErrorCodes.AccountLocked, "Too many failed logins. Try again later.")
        {
            RetryAfterMs = (long)Math.Ceiling((until - now).TotalMilliseconds),
        };
}
