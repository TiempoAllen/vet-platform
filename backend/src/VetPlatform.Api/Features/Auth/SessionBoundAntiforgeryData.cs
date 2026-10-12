using Microsoft.AspNetCore.Antiforgery;

namespace VetPlatform.Api.Features.Auth;

/// <summary>
/// Binds CSRF tokens to the sign-in session, not just the user: a token issued in one session (or to another browser
/// of the same user) is rejected in any other. Anonymous tokens carry an empty value.
/// </summary>
public sealed class SessionBoundAntiforgeryData : IAntiforgeryAdditionalDataProvider
{
    public string GetAdditionalData(HttpContext context) =>
        AuthClaims.GetSessionId(context.User)?.ToString("N") ?? string.Empty;

    public bool ValidateAdditionalData(HttpContext context, string additionalData) =>
        string.Equals(additionalData, GetAdditionalData(context), StringComparison.Ordinal);
}
