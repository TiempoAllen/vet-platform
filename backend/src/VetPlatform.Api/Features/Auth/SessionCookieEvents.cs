using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Auth;

/// <summary>
/// Validates the session behind every authenticated request: it must exist, not be revoked, be within its idle and
/// absolute lifetime, and belong to an active user of an active clinic. Otherwise the cookie is rejected and
/// cleared. Also turns the cookie handler's login/access-denied redirects into plain 401/403 API responses.
/// </summary>
public sealed class SessionCookieEvents(
    AppDbContext db,
    TimeProvider timeProvider,
    IOptions<AuthOptions> options,
    ILogger<SessionCookieEvents> logger) : CookieAuthenticationEvents
{
    /// <summary>Limits last-seen writes to one per session per minute.</summary>
    private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromMinutes(1);

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var sessionId = context.Principal is null ? null : AuthClaims.GetSessionId(context.Principal);
        var session = sessionId is null
            ? null
            : await db.UserSessions
                .Include(s => s.User)
                .ThenInclude(user => user!.Clinic)
                .SingleOrDefaultAsync(s => s.Id == sessionId, context.HttpContext.RequestAborted);

        var now = timeProvider.GetUtcNow();
        if (!IsUsable(session, now, options.Value))
        {
            logger.LogInformation("Rejected session cookie for session {SessionId}.", sessionId);
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        if (now - session!.LastSeenAt >= LastSeenWriteInterval)
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(context.HttpContext.RequestAborted);
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    public static bool IsUsable(UserSession? session, DateTimeOffset now, AuthOptions options) =>
        session is { RevokedAt: null, User: { Status: UserStatus.Active, Clinic.Status: ClinicStatus.Active } }
        && now < session.ExpiresAt
        && now < session.LastSeenAt + options.IdleTimeout;
}
