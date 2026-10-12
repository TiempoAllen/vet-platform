using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VetPlatform.Api.Common.Persistence;

namespace VetPlatform.Api.Tests.TestSupport;

/// <summary>
/// An API host backed by its own empty PostgreSQL database (see <see cref="TestPostgres"/>), dropped on dispose.
/// The schema is not created automatically: call <see cref="MigrateAsync"/> when a test needs it.
/// </summary>
public sealed class DatabaseApiFactory : ApiFactory, IAsyncLifetime
{
    private string? _connectionString;

    protected override string? ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("The test database has not been created yet.");

    public async Task InitializeAsync() => _connectionString = await TestPostgres.CreateDatabaseAsync();

    public async Task MigrateAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        if (_connectionString is not null)
        {
            await TestPostgres.DropDatabaseAsync(_connectionString);
        }
    }
}
