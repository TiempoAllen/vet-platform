using Microsoft.EntityFrameworkCore;

namespace VetPlatform.Api.Common.Persistence;

/// <summary>
/// The single EF Core context for the modular monolith. Feature modules add their entities by putting an
/// <see cref="IEntityTypeConfiguration{TEntity}"/> in their feature folder; it is picked up automatically.
/// Schema conventions (snake_case names, UUID ids, timestamptz) are recorded in docs/ARCHITECTURE.md ADR-002.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
