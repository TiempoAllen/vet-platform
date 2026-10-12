using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>docs/SECURITY.md: avoid logging personal/clinical data. Search terms arrive in query strings.</summary>
public sealed class RequestLoggingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task QueryStringValues_AreNeverLogged()
    {
        await factory.CreateClient().GetAsync("/api/test-probe/ok?search=jane.doe@example.com");

        // Non-vacuous: the request itself was logged...
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("/api/test-probe/ok"));
        // ...but nothing from the query string made it into any log entry (ours or the framework's).
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains("jane.doe"));
    }
}
