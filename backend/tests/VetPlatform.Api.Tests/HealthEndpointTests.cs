using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Health with the database unreachable. The healthy case is in <see cref="DatabaseTests"/>.</summary>
public sealed class HealthEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_Returns503Unhealthy_WhenDatabaseIsUnreachable()
    {
        var response = await factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());
        Assert.Single(body.EnumerateObject()); // no dependency names, hosts or error text
    }
}
