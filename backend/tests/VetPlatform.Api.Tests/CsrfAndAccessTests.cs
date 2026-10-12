using System.Net;
using System.Net.Http.Json;
using System.Text;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Security;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>CSRF and anonymous-access behaviour that needs no database.</summary>
public sealed class CsrfAndAccessTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task CsrfEndpoint_IssuesReadableRequestToken_AndHttpOnlyCookieToken()
    {
        var response = await factory.CreateClient().GetAsync("/api/auth/csrf");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var requestToken = CsrfClientExtensions.FindSetCookieHeader(response, CsrfProtection.RequestTokenCookie);
        Assert.NotNull(requestToken);
        Assert.DoesNotContain("httponly", requestToken, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", requestToken, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", requestToken, StringComparison.OrdinalIgnoreCase);

        var cookieToken = CsrfClientExtensions.FindSetCookieHeader(response, CsrfProtection.CookieTokenCookie);
        Assert.NotNull(cookieToken);
        Assert.Contains("httponly", cookieToken, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookieToken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsafeRequest_WithoutCsrfToken_Returns403CsrfEnvelope()
    {
        var response = await factory.CreateClient()
            .PostAsync("/api/test-probe/validate", Json("""{"name": "Rex", "age": 3}"""));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ErrorCodes.CsrfTokenInvalid, error!.Error.Code);
    }

    [Fact]
    public async Task UnsafeRequest_WithForgedCsrfToken_IsRejected()
    {
        var client = await factory.CreateClientWithCsrfAsync();
        client.DefaultRequestHeaders.Remove(CsrfProtection.HeaderName);
        client.DefaultRequestHeaders.Add(CsrfProtection.HeaderName, "forged-token");

        var response = await client.PostAsync("/api/test-probe/validate", Json("""{"name": "Rex", "age": 3}"""));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithoutCsrfToken_IsRejectedBeforeCredentialsAreChecked()
    {
        var response = await factory.CreateClient()
            .PostAsync("/api/auth/login", Json("""{"email": "a@example.com", "password": "x"}"""));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ErrorCodes.CsrfTokenInvalid, error!.Error.Code);
    }

    [Theory]
    [InlineData("""{"email": "not-an-email", "password": "x"}""", "email")]
    [InlineData("""{"email": "a@example.com"}""", "password")]
    public async Task Login_WithInvalidFields_Returns422(string body, string field)
    {
        var client = await factory.CreateClientWithCsrfAsync();

        var response = await client.PostAsync("/api/auth/login", Json(body));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Contains(error!.Error.Details, detail => detail.Field == field);
    }

    [Fact]
    public async Task Me_WithoutSession_Returns401Envelope_NotARedirect()
    {
        var response = await factory.CreateClient().GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ErrorCodes.Unauthenticated, error!.Error.Code);
    }

    [Fact]
    public async Task ControllerEndpoints_RequireSignInByDefault()
    {
        var response = await factory.CreateClient().GetAsync("/api/test-probe-protected");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ErrorCodes.Unauthenticated, error!.Error.Code);
    }

    [Fact]
    public async Task Me_WithGarbageSessionCookie_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", "__Host-vet_session=not-a-real-ticket");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
