using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using VetPlatform.Api.Common.Errors;

namespace VetPlatform.Api.Common.Security;

/// <summary>
/// CSRF protection for cookie-authenticated requests (ADR-003), using ASP.NET Core antiforgery tokens:
/// <c>GET /api/auth/csrf</c> issues a JavaScript-readable <see cref="RequestTokenCookie"/> cookie, and every unsafe
/// request (POST/PUT/PATCH/DELETE) must echo it in the <see cref="HeaderName"/> header. Tokens are bound to the
/// signed-in user, so they are reissued on sign-in and sign-out.
/// </summary>
public static class CsrfProtection
{
    public const string HeaderName = "X-XSRF-TOKEN";
    public const string RequestTokenCookie = "XSRF-TOKEN";

    /// <summary>The antiforgery cookie token (HttpOnly). The <c>__Host-</c> prefix pins it to this host over HTTPS.</summary>
    public const string CookieTokenCookie = "__Host-vet_csrf";

    public static void Configure(AntiforgeryOptions options)
    {
        options.HeaderName = HeaderName;
        options.Cookie.Name = CookieTokenCookie;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
    }

    /// <summary>Issues a request token for the current <see cref="HttpContext.User"/>.</summary>
    public static void IssueToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(RequestTokenCookie, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false, // the frontend reads it and sends it back in the header
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
    }
}

/// <summary>Global MVC filter: rejects unsafe requests without a valid antiforgery token with 403 CSRF_TOKEN_INVALID.</summary>
public sealed class CsrfProtectionFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) ||
            HttpMethods.IsTrace(method))
        {
            return;
        }

        if (!await antiforgery.IsRequestValidAsync(context.HttpContext))
        {
            context.Result = new ObjectResult(ErrorResponseWriter.Build(
                context.HttpContext,
                ErrorCodes.CsrfTokenInvalid,
                "The request is missing a valid CSRF token. Fetch a new one from /api/auth/csrf and retry."))
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }
    }
}
