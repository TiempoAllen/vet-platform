using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using VetPlatform.Api.Features.Auth;
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

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task EveryUnsafeMethod_RequiresACsrfToken(string method)
    {
        var withoutToken = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/test-probe/item"));
        var withToken = await (await factory.CreateClientWithCsrfAsync())
            .SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/test-probe/item"));

        Assert.Equal(HttpStatusCode.Forbidden, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, withToken.StatusCode);
    }

    /// <summary>
    /// CSRF protection is an MVC filter, so it only covers controller actions. This fails if anyone maps an unsafe
    /// minimal-API endpoint (MapPost, MapDelete, ...), which would silently skip it.
    /// </summary>
    [Fact]
    public void EveryUnsafeEndpoint_IsAControllerAction_SoTheCsrfFilterCoversIt()
    {
        string[] safe = ["GET", "HEAD", "OPTIONS", "TRACE"];
        var unprotected = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not { } methods
                || methods.HttpMethods.Any(method => !safe.Contains(method)))
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        // Health only answers GET in practice, but MapHealthChecks registers it for every method.
        Assert.Equal(["/api/health"], unprotected);
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

    [Theory]
    [InlineData("{bad")]
    [InlineData("")]
    public async Task Login_WithMalformedBody_Returns400(string body)
    {
        var client = await factory.CreateClientWithCsrfAsync();

        var response = await client.PostAsync("/api/auth/login", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void LoginRateLimit_GroupsIpv6ClientsByTheirSlash64(string address, string expectedPartition)
    {
        Assert.Equal(
            expectedPartition,
            AuthServiceCollectionExtensions.ClientPartitionKey(System.Net.IPAddress.Parse(address)));
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
