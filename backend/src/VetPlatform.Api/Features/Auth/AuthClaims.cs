using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Auth;

/// <summary>
/// The claims carried in the session cookie. They are only trusted after <see cref="SessionCookieEvents"/> has
/// checked the session row on the same request.
/// </summary>
public static class AuthClaims
{
    public const string SessionId = "sid";
    public const string ClinicId = "clinic_id";

    public static ClaimsPrincipal CreatePrincipal(User user, UserSession session) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(SessionId, session.Id.ToString()),
                new Claim(ClinicId, user.ClinicId.ToString()),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));

    public static Guid? GetUserId(ClaimsPrincipal principal) => Read(principal, ClaimTypes.NameIdentifier);

    public static Guid? GetSessionId(ClaimsPrincipal principal) => Read(principal, SessionId);

    private static Guid? Read(ClaimsPrincipal principal, string type) =>
        Guid.TryParse(principal.FindFirstValue(type), out var value) ? value : null;
}
