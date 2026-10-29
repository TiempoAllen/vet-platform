using System.Reflection;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Attachments;
using VetPlatform.Api.Features.Billing;
using VetPlatform.Api.Features.ClinicalNotes;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Clients;
using VetPlatform.Api.Features.Consultations;
using VetPlatform.Api.Features.Patients;
using VetPlatform.Api.Features.Scheduling;
using VetPlatform.Api.Features.Vaccinations;
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
/// <para>
/// Child rows that are not themselves <see cref="IClinicOwned"/> (user roles, later invoice items, ...) get no filter,
/// so they are not exposed as <see cref="DbSet{TEntity}"/>s: reach them only through their clinic-owned parent.
/// <see cref="UserSessions"/> is the documented exception, used only by authentication code keyed by session or user.
/// </para>
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

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Client> Clients => Set<Client>();

    public DbSet<Patient> Patients => Set<Patient>();

    public DbSet<WorkingHoursBlock> WorkingHours => Set<WorkingHoursBlock>();

    public DbSet<ScheduleException> ScheduleExceptions => Set<ScheduleException>();

    public DbSet<Appointment> Appointments => Set<Appointment>();

    public DbSet<Consultation> Consultations => Set<Consultation>();

    public DbSet<ConsultationAddendum> ConsultationAddenda => Set<ConsultationAddendum>();

    public DbSet<Diagnosis> Diagnoses => Set<Diagnosis>();

    public DbSet<Treatment> Treatments => Set<Treatment>();

    public DbSet<Prescription> Prescriptions => Set<Prescription>();

    public DbSet<Vaccination> Vaccinations => Set<Vaccination>();

    public DbSet<ClinicalNote> ClinicalNotes => Set<ClinicalNote>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<TaxRate> TaxRates => Set<TaxRate>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<InvoiceNumberSequence> InvoiceNumberSequences => Set<InvoiceNumberSequence>();

    /// <summary>Name of the tenant query filter. Named so entities can add other filters (e.g. soft delete) beside it.</summary>
    public const string ClinicFilter = "Clinic";

    /// <summary>Read by the query filters on every query; <see cref="Guid.Empty"/> matches no row.</summary>
    private Guid CurrentClinicId => tenant.ClinicId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // btree_gist lets an exclusion constraint combine "same veterinarian" (=) with "overlapping time" (&&) (B-09).
        modelBuilder.HasPostgresExtension("btree_gist");
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
        modelBuilder.Entity<TEntity>().HasQueryFilter(ClinicFilter, entity => entity.ClinicId == CurrentClinicId);
}
