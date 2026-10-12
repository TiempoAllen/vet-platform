using VetPlatform.Api.Common.Security;

namespace VetPlatform.Api.Tests.TestSupport;

/// <summary>Does what the frontend will do: read the XSRF-TOKEN cookie and echo it in the X-XSRF-TOKEN header.</summary>
public static class CsrfClientExtensions
{
    public static async Task RefreshCsrfTokenAsync(this HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/csrf");
        response.EnsureSuccessStatusCode();
        client.UseCsrfTokenFrom(response);
    }

    /// <summary>Picks up a reissued token (sign-in and sign-out reissue it). No-op if the response has none.</summary>
    public static void UseCsrfTokenFrom(this HttpClient client, HttpResponseMessage response)
    {
        var token = FindCookie(response, CsrfProtection.RequestTokenCookie);
        if (token is null)
        {
            return;
        }

        client.DefaultRequestHeaders.Remove(CsrfProtection.HeaderName);
        client.DefaultRequestHeaders.Add(CsrfProtection.HeaderName, Uri.UnescapeDataString(token));
    }

    /// <summary>Returns the value of a cookie set by the response, or null.</summary>
    public static string? FindCookie(HttpResponseMessage response, string name) =>
        FindSetCookieHeader(response, name) is { } header ? header[(name.Length + 1)..].Split(';')[0] : null;

    /// <summary>Returns the full Set-Cookie header for a cookie (value and attributes), or null.</summary>
    public static string? FindSetCookieHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var headers)
            ? headers.LastOrDefault(header => header.StartsWith(name + "=", StringComparison.Ordinal))
            : null;
}
