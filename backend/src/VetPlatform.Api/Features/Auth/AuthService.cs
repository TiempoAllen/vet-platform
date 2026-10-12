using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Auth;

/// <summary>
/// Sign-in, sign-out and password-change rules (docs/SECURITY.md section 2, ADR-003). Every failed sign-in (unknown
/// email, wrong password, locked, disabled user, suspended clinic) returns the same 401 after the same work (one
/// password hash, one failed-attempt UPDATE and one audit INSERT), so neither the response nor its timing reveals
/// whether an account exists or why it was refused. Logs and audit metadata carry ids, never emails or passwords.
/// </summary>
public sealed class AuthService(
    AppDbContext db,
    IPasswordHasher<User> passwordHasher,
    AuditTrail audit,
    TimeProvider timeProvider,
    IOptions<AuthOptions> options,
    ILogger<AuthService> logger)
{
    /// <summary>Hash checked when the email is unknown, so that case takes as long as a wrong password.</summary>
    private static readonly Lazy<string> DummyPasswordHash =
        new(() => new PasswordHasher<User>().HashPassword(null!, Guid.NewGuid().ToString()));

    /// <summary>Matches no row: lets failure paths that must not count an attempt still run the same UPDATE.</summary>
    private static readonly Guid NoUser = Guid.Empty;

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

        // Sign-in happens before any clinic is known, and email is unique across clinics (ADR-003).
        var user = await db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Clinic)
            .Include(u => u.Roles)
            .SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (user is null)
        {
            passwordHasher.VerifyHashedPassword(null!, DummyPasswordHash.Value, password);
            await FailAsync(NoUser, null, "unknown_account", now, cancellationToken);
            logger.LogInformation("Sign-in failed: unknown account.");
            throw InvalidCredentials();
        }

        if (user.LockedUntil > now)
        {
            // Attempts during a lock are not counted, so they cannot extend it.
            passwordHasher.VerifyHashedPassword(user, DummyPasswordHash.Value, password);
            await FailAsync(NoUser, user, "locked", now, cancellationToken);
            logger.LogInformation("Sign-in refused for user {UserId}: account temporarily locked.", user.Id);
            throw InvalidCredentials();
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            if (await FailAsync(user.Id, user, "wrong_password", now, cancellationToken))
            {
                logger.LogWarning(
                    "User {UserId} locked for {LockoutMinutes} minutes after repeated failed sign-ins.",
                    user.Id,
                    settings.LockoutDuration.TotalMinutes);
            }
            else
            {
                logger.LogInformation("Sign-in failed for user {UserId}: wrong password.", user.Id);
            }

            throw InvalidCredentials();
        }

        if (user.Status != UserStatus.Active || user.Clinic!.Status != ClinicStatus.Active)
        {
            await FailAsync(NoUser, user, "inactive", now, cancellationToken);
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
        audit.Record(
            AuditActions.LoginSucceeded, nameof(User), user.Id,
            new { sessionId = session.Id }, actorUserId: user.Id, clinicId: user.ClinicId);

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} signed in (session {SessionId}).", user.Id, session.Id);
        return (user, session);
    }

    /// <summary>
    /// Records a failed sign-in: counts it against <paramref name="countAgainst"/> (or nothing for <see cref="NoUser"/>)
    /// and writes the audit entry. Returns true when this attempt locked the account.
    /// </summary>
    private async Task<bool> FailAsync(
        Guid countAgainst,
        User? user,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var locked = await RecordFailedAttemptAsync(countAgainst, now, cancellationToken);

        audit.Record(
            AuditActions.LoginFailed, user is null ? null : nameof(User), user?.Id,
            new { reason }, actorUserId: null, clinicId: user?.ClinicId);
        if (locked)
        {
            audit.Record(
                AuditActions.AccountLocked, nameof(User), user!.Id,
                new { until = now + options.Value.LockoutDuration }, actorUserId: null, clinicId: user.ClinicId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return locked;
    }

    /// <summary>
    /// Counts a wrong password in one atomic UPDATE, locking the account when the limit is reached, and reports
    /// whether this attempt locked it. PostgreSQL serialises concurrent updates of the row, so parallel guesses
    /// cannot overwrite each other's increments. Raw SQL so the lock decision comes back in the same statement.
    /// </summary>
    private async Task<bool> RecordFailedAttemptAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var limit = settings.MaxFailedLogins;
        var lockedUntil = now + settings.LockoutDuration;

        var result = await db.Database.SqlQuery<bool>($"""
            UPDATE users SET
                locked_until = CASE WHEN failed_login_count + 1 >= {limit} THEN {lockedUntil} ELSE locked_until END,
                failed_login_count = CASE WHEN failed_login_count + 1 >= {limit} THEN 0 ELSE failed_login_count + 1 END,
                updated_at = {now}
            WHERE id = {userId} AND (locked_until IS NULL OR locked_until <= {now})
            RETURNING locked_until IS NOT NULL AND locked_until > {now} AS "Value"
            """).ToListAsync(cancellationToken);

        return result is [true];
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
            audit.Record(
                AuditActions.Logout, nameof(UserSession), sessionId,
                actorUserId: AuthClaims.GetUserId(principal), clinicId: AuthClaims.GetClinicId(principal));
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Session {SessionId} signed out.", sessionId);
        }
    }

    /// <summary>
    /// Changes the signed-in user's password after re-checking the current one, and signs out every other session
    /// of that user (a stolen session must not survive a password change).
    /// </summary>
    public async Task ChangePasswordAsync(
        ClaimsPrincipal principal,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == AuthClaims.GetUserId(principal), cancellationToken)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, "Authentication is required.");

        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.",
                [new ApiErrorDetail("currentPassword", "The current password is incorrect.")]);
        }

        if (PasswordPolicy.Validate(newPassword, user.Email) is { } problem)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.",
                [new ApiErrorDetail("newPassword", problem)]);
        }

        var now = timeProvider.GetUtcNow();
        var currentSession = AuthClaims.GetSessionId(principal);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        user.PasswordHash = passwordHasher.HashPassword(user, newPassword);
        var otherSessions = await db.UserSessions
            .Where(session => session.UserId == user.Id && session.Id != currentSession && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(session => session.RevokedAt, now)
                    .SetProperty(session => session.UpdatedAt, now),
                cancellationToken);
        audit.Record(AuditActions.PasswordChanged, nameof(User), user.Id, new { otherSessionsSignedOut = otherSessions });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("User {UserId} changed their password; {Count} other sessions signed out.", user.Id, otherSessions);
    }

    public Task<User?> FindCurrentUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var userId = AuthClaims.GetUserId(principal);
        return db.Users
            .AsNoTracking()
            .Include(user => user.Clinic)
            .Include(user => user.Roles)
            .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
    }
}
