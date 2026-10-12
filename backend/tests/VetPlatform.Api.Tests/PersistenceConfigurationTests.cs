using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Persistence checks that need no running database.</summary>
public sealed class PersistenceConfigurationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void Model_HasNoChangesMissingFromMigrations()
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database;

        // Fails when an entity or mapping changed without `dotnet ef migrations add`.
        Assert.False(database.HasPendingModelChanges());
    }

    [Fact]
    public void Startup_Fails_WhenConnectionStringIsMissing()
    {
        using var unconfigured = new UnconfiguredDatabaseApiFactory();

        var exception = Record.Exception(() => unconfigured.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("ConnectionStrings:Default", Flatten(exception));
    }

    private static string Flatten(Exception exception) =>
        exception.InnerException is null ? exception.Message : exception.Message + " | " + Flatten(exception.InnerException);
}
