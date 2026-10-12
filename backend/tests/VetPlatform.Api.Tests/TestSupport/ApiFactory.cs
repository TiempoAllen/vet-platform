using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    public CapturingLoggerProvider Logs { get; } = new();

    protected virtual string? ConnectionString => UnreachableConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Added last, so it overrides any ConnectionStrings__Default set in the developer's environment.
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString }));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(Logs);
            services.AddControllers().AddApplicationPart(typeof(ApiFactory).Assembly);
        });
    }
}

/// <summary>An API host whose connection string is empty, to exercise the startup configuration check.</summary>
public sealed class UnconfiguredDatabaseApiFactory : ApiFactory
{
    protected override string? ConnectionString => "";
}
