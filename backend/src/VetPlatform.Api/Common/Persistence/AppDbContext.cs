using System.Reflection;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Common.Persistence;

/// <summary>
/// The single EF Core context for the modular monolith. Feature modules add their entities by putting an
/// <see cref="IEntityTypeConfiguration{TEntity}"/> in their feature folder; it is picked up automatically.
/// Schema conventions (snake_case names, UUID ids, timestamptz) are recorded in docs/ARCHITECTURE.md ADR-002.
/// </summary>
/// <remarks>
/// Tenant isolation (ADR-004): every <see cref="IClinicOwned"/> entity gets a query filter limiting it to the current
/// request's clinic. With no current clinic the filter matches nothing, so a missing tenant fails closed. Code that
/// must work across clinics (sign-in, session validation, the CLI) opts out explicitly with
/// <c>IgnoreQueryFilters()</c> and says why in a comment.
/// </remarks>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant) : DbContext(options)
{
    /// <summary>
    /// ICU collation that compares case-insensitively (but accent-sensitively). Use it on columns whose uniqueness
    /// and equality must ignore case, such as email, so the database enforces it whoever writes the row.
    /// </summary>
    public const string CaseInsensitiveCollation = "case_insensitive";

    public DbSet<Clinic> Clinics => Set<Clinic>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>Read by the query filters on every query; <see cref="Guid.Empty"/> matches no row.</summary>
    private Guid CurrentClinicId => tenant.ClinicId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasCollation(CaseInsensitiveCollation, locale: "und-u-ks-level2", provider: "icu", deterministic: false);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        var applyClinicFilter = typeof(AppDbContext).GetMethod(
            nameof(ApplyClinicFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(type => typeof(IClinicOwned).IsAssignableFrom(type.ClrType)))
        {
            applyClinicFilter.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }

    private void ApplyClinicFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, IClinicOwned =>
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.ClinicId == CurrentClinicId);
}
