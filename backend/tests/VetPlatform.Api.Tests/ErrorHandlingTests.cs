using System.Net;
using System.Net.Http.Json;
using System.Text;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Every failure must use the docs/API.md envelope and never leak internals.</summary>
public sealed class ErrorHandlingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<ApiErrorResponse> ReadError(HttpResponseMessage response)
    {
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(
            response.Headers.GetValues(RequestIdMiddleware.HeaderName).Single(),
            error.RequestId);
        return error;
    }

    [Fact]
    public async Task UnknownRoute_Returns404Envelope()
    {
        var response = await factory.CreateClient().GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, (await ReadError(response)).Error.Code);
    }

    [Fact]
    public async Task ControllerReturningBareNotFound_UsesEnvelope_NotProblemDetails()
    {
        var response = await factory.CreateClient().GetAsync("/api/test-probe/bare-not-found");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, (await ReadError(response)).Error.Code);
    }

    [Fact]
    public async Task WrongHttpMethod_Returns405Envelope()
    {
        var response = await factory.CreateClient().PostAsync("/api/test-probe/throw", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(ErrorCodes.MethodNotAllowed, (await ReadError(response)).Error.Code);
    }

    [Fact]
    public async Task UnexpectedException_Returns500_WithoutLeakingDetails_ButIsLogged()
    {
        var response = await factory.CreateClient().GetAsync("/api/test-probe/throw");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(TestProbeController.SecretMessage, raw);
        Assert.DoesNotContain("InvalidOperationException", raw);
        Assert.DoesNotContain(" at ", raw); // no stack trace frames

        var error = await ReadError(response);
        Assert.Equal(ErrorCodes.InternalError, error.Error.Code);

        Assert.Contains(factory.Logs.Entries, entry => entry.Contains(TestProbeController.SecretMessage));
    }

    [Fact]
    public async Task ApiException_UsesItsStatusAndCode()
    {
        var response = await factory.CreateClient().GetAsync("/api/test-probe/conflict");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ReadError(response);
        Assert.Equal(ErrorCodes.Conflict, error.Error.Code);
        Assert.Equal("Already exists.", error.Error.Message);
    }

    [Fact]
    public async Task InvalidFields_Return422_WithFieldDetails()
    {
        var response = await factory.CreateClient()
            .PostAsync("/api/test-probe/validate", Json("""{"age": 99}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await ReadError(response);
        Assert.Equal(ErrorCodes.ValidationError, error.Error.Code);
        Assert.Contains(error.Error.Details, detail => detail.Field == "name");
        Assert.Contains(error.Error.Details, detail => detail.Field == "age");
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("")]
    public async Task MalformedOrEmptyBody_Returns400(string body)
    {
        var response = await factory.CreateClient().PostAsync("/api/test-probe/validate", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ReadError(response);
        Assert.Equal(ErrorCodes.BadRequest, error.Error.Code);
        Assert.Empty(error.Error.Details); // parser internals are not echoed
    }

    [Fact]
    public async Task UnsupportedContentType_Returns415Envelope()
    {
        var content = new StringContent("x", Encoding.UTF8, "text/plain");

        var response = await factory.CreateClient().PostAsync("/api/test-probe/validate", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedMediaType, (await ReadError(response)).Error.Code);
    }

    [Fact]
    public async Task ValidRequest_Succeeds()
    {
        var response = await factory.CreateClient()
            .PostAsync("/api/test-probe/validate", Json("""{"name": "Rex", "age": 3}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
