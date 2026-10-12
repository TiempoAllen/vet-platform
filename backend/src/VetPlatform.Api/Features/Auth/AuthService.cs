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
/// <remarks>
/// Sign-in must not depend on whoever the browser is already signed in as: callers clear the request principal first
/// (see <see cref="AuthController"/>), and sign-in audit entries name their actor and clinic explicitly.
/// </remarks>
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
            await FailAsync(NoUser, null, AuditActions.LoginFailed, "unknown_account", now, cancellationToken);
            logger.LogInformation("Sign-in failed: unknown account.");
            throw InvalidCredentials();
        }

        if (user.LockedUntil > now)
        {
            // Attempts during a lock are not counted, so they cannot extend it.
            passwordHasher.VerifyHashedPassword(user, DummyPasswordHash.Value, password);
            await FailAsync(NoUser, user, AuditActions.LoginFailed, "locked", now, cancellationToken);
            logger.LogInformation("Sign-in refused for user {UserId}: account temporarily locked.", user.Id);
            throw InvalidCredentials();
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            await FailAsync(user.Id, user, AuditActions.LoginFailed, "wrong_password", now, cancellationToken);
            logger.LogInformation("Sign-in failed for user {UserId}: wrong password.", user.Id);
            throw InvalidCredentials();
        }

        if (user.Status != UserStatus.Active || user.Clinic!.Status != ClinicStatus.Active)
        {
            await FailAsync(NoUser, user, AuditActions.LoginFailed, "inactive", now, cancellationToken);
            logger.LogInformation("Sign-in refused for user {UserId}: user or clinic is not active.", user.Id);
            throw InvalidCredentials();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Serialise with a concurrent password change: if the password changed after it was verified above, the old
        // password must not open a session that the change's "sign out other sessions" step has already missed.
        if (await LockPasswordHashAsync(user.Id, cancellationToken) != user.PasswordHash)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation("Sign-in for user {UserId} raced a password change; refused.", user.Id);
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
        audit.RecordFor(
            AuditActions.LoginSucceeded, nameof(User), user.Id, new { sessionId = session.Id },
            actorUserId: user.Id, clinicId: user.ClinicId);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("User {UserId} signed in (session {SessionId}).", user.Id, session.Id);
        return (user, session);
    }

    /// <summary>
    /// Records a failed credential check: counts it against <paramref name="countAgainst"/> (nothing for
    /// <see cref="NoUser"/>) and writes the audit entries. The attempt is attributed to no actor: whoever made it is
    /// not authenticated as the account. Returns true when this attempt locked the account.
    /// </summary>
    private async Task<bool> FailAsync(
        Guid countAgainst,
        User? user,
        string action,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var locked = await RecordFailedAttemptAsync(countAgainst, now, cancellationToken);

        audit.RecordFor(
            action, user is null ? null : nameof(User), user?.Id, new { reason },
            actorUserId: null, clinicId: user?.ClinicId);
        if (locked)
        {
            audit.RecordFor(
                AuditActions.AccountLocked, nameof(User), user!.Id, new { until = now + options.Value.LockoutDuration },
                actorUserId: null, clinicId: user.ClinicId);
            logger.LogWarning(
                "User {UserId} locked for {LockoutMinutes} minutes after repeated failed password checks.",
                user.Id,
                options.Value.LockoutDuration.TotalMinutes);
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

    /// <summary>Locks the user row until the current transaction ends and returns its stored password hash.</summary>
    private async Task<string> LockPasswordHashAsync(Guid userId, CancellationToken cancellationToken) =>
        (await db.Database
            .SqlQuery<string>($"""SELECT password_hash AS "Value" FROM users WHERE id = {userId} FOR UPDATE""")
            .ToListAsync(cancellationToken)).Single();

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
            audit.RecordFor(
                AuditActions.Logout, nameof(UserSession), sessionId, metadata: null,
                actorUserId: AuthClaims.GetUserId(principal), clinicId: AuthClaims.GetClinicId(principal));
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Session {SessionId} signed out.", sessionId);
        }
    }

    /// <summary>
    /// Changes the signed-in user's password after re-checking the current one, and signs out every other session
    /// of that user (a stolen session must not survive a password change). Wrong current passwords count towards
    /// the account lockout exactly like failed sign-ins; if one triggers the lock, every session of the user,
    /// including this one, is signed out, so a stolen session cannot be used to guess the password.
    /// </summary>
    public async Task ChangePasswordAsync(
        ClaimsPrincipal principal,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == AuthClaims.GetUserId(principal), cancellationToken)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, "Authentication is required.");
        var now = timeProvider.GetUtcNow();

        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
        {
            if (await FailAsync(user.Id, user, AuditActions.PasswordChangeFailed, "wrong_current_password", now, cancellationToken))
            {
                await RevokeAllSessionsAsync(user, exceptSessionId: null, "lockout", now, cancellationToken);
                throw new ApiException(
                    StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated,
                    "Too many incorrect passwords. You have been signed out.");
            }

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

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPasswordHashAsync(user.Id, cancellationToken); // serialise with concurrent sign-ins
        user.PasswordHash = passwordHasher.HashPassword(user, newPassword);
        user.FailedLoginCount = 0;
        var otherSessions = await RevokeAllSessionsAsync(
            user, exceptSessionId: AuthClaims.GetSessionId(principal), reason: null, now, cancellationToken);
        audit.Record(AuditActions.PasswordChanged, nameof(User), user.Id, new { otherSessionsSignedOut = otherSessions });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("User {UserId} changed their password; {Count} other sessions signed out.", user.Id, otherSessions);
    }

    /// <summary>
    /// Revokes all of a user's active sessions except <paramref name="exceptSessionId"/>. With a
    /// <paramref name="reason"/>, also audits and saves; otherwise the caller audits as part of its own change.
    /// </summary>
    public async Task<int> RevokeAllSessionsAsync(
        User user,
        Guid? exceptSessionId,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var revoked = await db.UserSessions
            .Where(session => session.UserId == user.Id && session.Id != exceptSessionId && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(session => session.RevokedAt, now)
                    .SetProperty(session => session.UpdatedAt, now),
                cancellationToken);

        if (reason is not null && revoked > 0)
        {
            audit.RecordFor(
                AuditActions.SessionsRevoked, nameof(User), user.Id, new { reason, count = revoked },
                actorUserId: null, clinicId: user.ClinicId);
            await db.SaveChangesAsync(cancellationToken);
        }

        return revoked;
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
