using System.Net.Http.Json;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

public sealed class RequestIdTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Response_CarriesGeneratedRequestId()
    {
        var response = await factory.CreateClient().GetAsync("/api/test-probe/ok");

        Assert.True(response.Headers.TryGetValues(RequestIdMiddleware.HeaderName, out var values));
        Assert.Matches("^[0-9a-f]{32}$", values!.Single());
    }

    [Fact]
    public async Task SafeIncomingRequestId_IsEchoedInHeaderAndErrorBody()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/does-not-exist");
        request.Headers.Add(RequestIdMiddleware.HeaderName, "client-trace_1.2");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal("client-trace_1.2", response.Headers.GetValues(RequestIdMiddleware.HeaderName).Single());
        var body = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("client-trace_1.2", body!.RequestId);
    }

    [Theory]
    [InlineData("has spaces in it")]
    [InlineData("semi;colon")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")] // 70 chars
    public async Task UnsafeIncomingRequestId_IsReplaced(string incoming)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/test-probe/ok");
        request.Headers.TryAddWithoutValidation(RequestIdMiddleware.HeaderName, incoming);

        var response = await factory.CreateClient().SendAsync(request);

        var returned = response.Headers.GetValues(RequestIdMiddleware.HeaderName).Single();
        Assert.NotEqual(incoming, returned);
        Assert.Matches("^[0-9a-f]{32}$", returned);
    }
}
