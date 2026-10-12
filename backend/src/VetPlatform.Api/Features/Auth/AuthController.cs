using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Security;
using UserEntity = VetPlatform.Api.Features.Identity.User;

namespace VetPlatform.Api.Features.Auth;

/// <summary>Cookie-session authentication endpoints (docs/API.md section 3).</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService authService, IAntiforgery antiforgery) : ControllerBase
{
    public sealed record SignInRequest(
        [Required, EmailAddress, MaxLength(UserEntity.EmailMaxLength)] string? Email,
        [Required, MaxLength(1024)] string? Password);

    public sealed record ClinicSummary(Guid Id, string Name);

    public sealed record CurrentUserResponse(Guid Id, string Name, string Email, ClinicSummary Clinic)
    {
        public static CurrentUserResponse From(UserEntity user) =>
            new(user.Id, user.Name, user.Email, new ClinicSummary(user.Clinic!.Id, user.Clinic.Name));
    }

    /// <summary>Issues the CSRF request token cookie. Call before the first unsafe request, and after a 403 CSRF_TOKEN_INVALID.</summary>
    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult Csrf()
    {
        CsrfProtection.IssueToken(HttpContext, antiforgery);
        return NoContent();
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthServiceCollectionExtensions.LoginRateLimitPolicy)]
    public async Task<ActionResult<CurrentUserResponse>> Login(SignInRequest request, CancellationToken cancellationToken)
    {
        var (user, session) = await authService.SignInAsync(request.Email!, request.Password!, cancellationToken);

        // Signing in from a browser that already has a session replaces it: the old session is revoked, but only
        // once the new sign-in has succeeded, so a mistyped password does not sign the user out.
        await authService.RevokeSessionAsync(User, cancellationToken);

        var principal = AuthClaims.CreatePrincipal(user, session);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = false });

        HttpContext.User = principal;
        CsrfProtection.IssueToken(HttpContext, antiforgery);

        return Ok(CurrentUserResponse.From(user));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await authService.RevokeSessionAsync(User, cancellationToken);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        CsrfProtection.IssueToken(HttpContext, antiforgery);

        return NoContent();
    }

    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken)
    {
        var user = await authService.FindCurrentUserAsync(User, cancellationToken)
            ?? throw new ApiException(
                StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, "Authentication is required.");

        return Ok(CurrentUserResponse.From(user));
    }
}
