using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Common.Persistence;

/// <summary>
/// The single EF Core context for the modular monolith. Feature modules add their entities by putting an
/// <see cref="IEntityTypeConfiguration{TEntity}"/> in their feature folder; it is picked up automatically.
/// Schema conventions (snake_case names, UUID ids, timestamptz) are recorded in docs/ARCHITECTURE.md ADR-002.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Clinic> Clinics => Set<Clinic>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    /// <summary>
    /// ICU collation that compares case-insensitively (but accent-sensitively). Use it on columns whose uniqueness
    /// and equality must ignore case, such as email, so the database enforces it whoever writes the row.
    /// </summary>
    public const string CaseInsensitiveCollation = "case_insensitive";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasCollation(CaseInsensitiveCollation, locale: "und-u-ks-level2", provider: "icu", deterministic: false);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
