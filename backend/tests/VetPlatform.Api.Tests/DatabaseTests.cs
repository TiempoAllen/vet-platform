using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Needs PostgreSQL (Docker, or VETPLATFORM_TEST_POSTGRES). Filter with <c>--filter Category=Database</c>.</summary>
[Trait("Category", "Database")]
public sealed class DatabaseTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    [Fact]
    public async Task Health_IsAnonymousAndReportsHealthy_WhenDatabaseIsReachable()
    {
        var response = await factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Single(body.EnumerateObject());
    }

    [Fact]
    public async Task Migrations_ApplyToAnEmptyDatabase_AndLeaveNothingPending()
    {
        await factory.MigrateAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database;

        Assert.Empty(await database.GetPendingMigrationsAsync());
        Assert.Equal(database.GetMigrations(), await database.GetAppliedMigrationsAsync());

        var tables = await database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();
        Assert.Contains(PersistenceServiceCollectionExtensions.MigrationsHistoryTable, tables);
    }
}
