using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>
/// Rolling the B-13 migration back and forward (its own database, because it drops the prescriptions table). Down
/// restores the B-12 trigger functions that Up hardened (ADR-013).
/// </summary>
[Trait("Category", "Database")]
public sealed class PrescriptionsMigrationTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private async Task<string> DiagnosesTriggerAsync()
    {
        string definition = "";
        await factory.UpdateDatabaseAsync(async db => definition = await db.Database
            .SqlQueryRaw<string>("SELECT pg_get_functiondef('diagnoses_follow_consultation'::regproc) AS \"Value\"")
            .SingleAsync());
        return definition;
    }

    [Fact]
    public async Task RollingBackAndForward_RestoresAndReappliesTheTriggerFunctions()
    {
        await factory.MigrateAsync();
        Assert.Contains("cannot move to another consultation", await DiagnosesTriggerAsync());

        await using var scope = factory.Services.CreateAsyncScope();
        var migrator = scope.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>();
        await migrator.MigrateAsync("AddDiagnosesAndTreatments");

        Assert.DoesNotContain("cannot move to another consultation", await DiagnosesTriggerAsync());
        await factory.UpdateDatabaseAsync(async db => Assert.Equal(
            0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_proc WHERE proname = 'prescriptions_follow_consultation'").SingleAsync()));

        await migrator.MigrateAsync();
        Assert.Contains("cannot move to another consultation", await DiagnosesTriggerAsync());
    }
}
