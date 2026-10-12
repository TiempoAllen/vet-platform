using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Auth;

/// <summary>
/// Sign-in and sign-out rules (docs/SECURITY.md section 2, ADR-003). Every failed sign-in (unknown email, wrong
/// password, locked, disabled user, suspended clinic) returns the same 401, so responses never reveal whether an
/// account exists or why it was refused. Logs carry user ids, never email addresses or passwords.
/// </summary>
public sealed class AuthService(
    AppDbContext db,
    IPasswordHasher<User> passwordHasher,
    TimeProvider timeProvider,
    IOptions<AuthOptions> options,
    ILogger<AuthService> logger)
{
    /// <summary>Hash checked when the email is unknown, so that case takes as long as a wrong password.</summary>
    private static readonly Lazy<string> DummyPasswordHash =
        new(() => new PasswordHasher<User>().HashPassword(null!, Guid.NewGuid().ToString()));

    private static ApiException InvalidCredentials() =>
        new(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, "Email or password is incorrect.");

    public async Task<(User User, UserSession Session)> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var normalizedEmail = User.NormalizeEmail(email);

        var user = await db.Users
            .Include(u => u.Clinic)
            .SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (user is null)
        {
            passwordHasher.VerifyHashedPassword(null!, DummyPasswordHash.Value, password);
            logger.LogInformation("Sign-in failed: unknown account.");
            throw InvalidCredentials();
        }

        if (user.LockedUntil > now)
        {
            passwordHasher.VerifyHashedPassword(user, DummyPasswordHash.Value, password);
            logger.LogInformation("Sign-in refused for user {UserId}: account temporarily locked.", user.Id);
            throw InvalidCredentials();
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= settings.MaxFailedLogins)
            {
                user.LockedUntil = now + settings.LockoutDuration;
                user.FailedLoginCount = 0;
                logger.LogWarning(
                    "User {UserId} locked for {LockoutMinutes} minutes after repeated failed sign-ins.",
                    user.Id,
                    settings.LockoutDuration.TotalMinutes);
            }
            else
            {
                logger.LogInformation("Sign-in failed for user {UserId}: wrong password.", user.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
            throw InvalidCredentials();
        }

        if (user.Status != UserStatus.Active || user.Clinic!.Status != ClinicStatus.Active)
        {
            logger.LogInformation("Sign-in refused for user {UserId}: user or clinic is not active.", user.Id);
            throw InvalidCredentials();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;

        var session = new UserSession
        {
            UserId = user.Id,
            LastSeenAt = now,
            ExpiresAt = now + settings.AbsoluteLifetime,
        };
        db.UserSessions.Add(session);

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} signed in (session {SessionId}).", user.Id, session.Id);
        return (user, session);
    }

    /// <summary>Revokes the caller's session, if any. Safe to call when not signed in.</summary>
    public async Task RevokeSessionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (AuthClaims.GetSessionId(principal) is not { } sessionId)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var revoked = await db.UserSessions
            .Where(session => session.Id == sessionId && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(session => session.RevokedAt, now)
                    .SetProperty(session => session.UpdatedAt, now),
                cancellationToken);

        if (revoked > 0)
        {
            logger.LogInformation("Session {SessionId} signed out.", sessionId);
        }
    }

    public Task<User?> FindCurrentUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var userId = AuthClaims.GetUserId(principal);
        return db.Users
            .AsNoTracking()
            .Include(user => user.Clinic)
            .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
    }
}
