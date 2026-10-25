using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace VetPlatform.Api.Tests.TestSupport;

/// <summary>
/// Hosts the real API in memory. Registers this assembly's controllers (see <see cref="TestProbeController"/>)
/// so error paths can be exercised without adding test-only endpoints to the production project.
/// By default the database points at a closed local port, so tests that do not touch the database need no
/// PostgreSQL; use <see cref="DatabaseApiFactory"/> for tests that do.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=unreachable;Username=test;Timeout=3";

    public ApiFactory()
    {
        // Session and CSRF cookies are Secure (__Host- prefix), so the client must talk HTTPS to send them back.
        ClientOptions.BaseAddress = new Uri("https://localhost");
    }

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>The app's clock. Advance it to test idle timeouts, lifetimes and lockouts.</summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected virtual string? ConnectionString => UnreachableConnectionString;

    /// <summary>A private file store per factory, under the system temp directory.</summary>
    public string FileStorageRoot { get; } = Path.Combine(Path.GetTempPath(), "vetplatform-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Configuration overrides. Sign-in throttling is relaxed so tests can sign in freely.</summary>
    protected virtual IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Auth:LoginAttemptsPerMinute"] = "1000",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Added last, so it overrides any ConnectionStrings__Default set in the developer's environment.
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(Settings)
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["FileStorage:LocalRoot"] = FileStorageRoot,
            }));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(Logs);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddControllers().AddApplicationPart(typeof(ApiFactory).Assembly);
        });
    }

    /// <summary>A client that already holds a CSRF token, ready for unsafe requests.</summary>
    public async Task<HttpClient> CreateClientWithCsrfAsync()
    {
        var client = CreateClient();
        await client.RefreshCsrfTokenAsync();
        return client;
    }
}

/// <summary>An API host whose connection string is empty, to exercise the startup configuration check.</summary>
public sealed class UnconfiguredDatabaseApiFactory : ApiFactory
{
    protected override string? ConnectionString => "";
}
