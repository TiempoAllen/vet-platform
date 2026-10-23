using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Consultations;

/// <summary>
/// A diagnosis recorded in a consultation (docs/DATABASE.md section 5, ADR-012). Editable while the consultation is in
/// progress; after completion only its <see cref="Status"/> may change (by a veterinarian), e.g. when results arrive.
/// </summary>
public sealed class Diagnosis : Entity, IClinicOwned
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 50;
    public const int NotesMaxLength = 2000;
    public const int MaxPerConsultation = 50;

    /// <summary>Partial unique index: at most one primary diagnosis per consultation.</summary>
    public const string OnePrimaryIndex = "ux_diagnoses_one_primary_per_consultation";

    public Guid ClinicId { get; set; }

    public Guid ConsultationId { get; set; }

    /// <summary>The consultation's patient; the composite foreign key makes the database guarantee they match.</summary>
    public Guid PatientId { get; set; }

    public required string Name { get; set; }

    /// <summary>Optional code in an external terminology, stored as given (not validated yet).</summary>
    public string? Code { get; set; }

    /// <summary>E.g. "VeNom" or "SNOMED CT"; only together with <see cref="Code"/>.</summary>
    public string? CodeSystem { get; set; }

    public DiagnosisStatus Status { get; set; } = DiagnosisStatus.Provisional;

    public bool IsPrimary { get; set; }

    public string? Notes { get; set; }

    public Guid? CreatedByUserId { get; set; }
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum DiagnosisStatus
{
    /// <summary>Suspected or differential.</summary>
    Provisional,
    Confirmed,
    RuledOut,
}

/// <summary>
/// Care given during the consultation: a procedure, medication administered in the clinic, fluids, ... (ADR-012).
/// Take-home medication is a prescription (B-13). Editable only while the consultation is in progress.
/// </summary>
public sealed class Treatment : Entity, IClinicOwned
{
    public const int NameMaxLength = 200;
    public const int DetailMaxLength = 100;
    public const int NotesMaxLength = 2000;
    public const int MaxPerConsultation = 100;

    public Guid ClinicId { get; set; }

    public Guid ConsultationId { get; set; }

    public Guid PatientId { get; set; }

    /// <summary>Optional: the diagnosis of the same consultation this treats.</summary>
    public Guid? DiagnosisId { get; set; }

    public required string Name { get; set; }

    public TreatmentType Type { get; set; }

    public string? Dose { get; set; }

    public string? Route { get; set; }

    public string? Frequency { get; set; }

    public string? Duration { get; set; }

    public string? Notes { get; set; }

    public Guid? CreatedByUserId { get; set; }
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum TreatmentType
{
    Procedure,
    Medication,
    Fluids,
    Other,
}

internal sealed class DiagnosisConfiguration : IEntityTypeConfiguration<Diagnosis>
{
    public void Configure(EntityTypeBuilder<Diagnosis> builder)
    {
        builder.ToTable("diagnoses", table =>
        {
            table.HasCheckConstraint("ck_diagnoses_status", "status IN ('Provisional', 'Confirmed', 'RuledOut')");
            table.HasCheckConstraint("ck_diagnoses_name", "btrim(name) <> ''");
            table.HasCheckConstraint("ck_diagnoses_code_system", "code_system IS NULL OR code IS NOT NULL");
        });
        builder.Property(d => d.Name).HasMaxLength(Diagnosis.NameMaxLength);
        builder.Property(d => d.Code).HasMaxLength(Diagnosis.CodeMaxLength);
        builder.Property(d => d.CodeSystem).HasMaxLength(Diagnosis.CodeMaxLength);
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.Notes).HasMaxLength(Diagnosis.NotesMaxLength);

        // Same clinic, same consultation, same patient: one composite key does all three (ADR-005, ADR-012).
        builder.HasOne<Consultation>().WithMany(c => c.Diagnoses)
            .HasForeignKey(d => new { d.ClinicId, d.PatientId, d.ConsultationId })
            .HasPrincipalKey(c => new { c.ClinicId, c.PatientId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany()
            .HasForeignKey(d => new { d.ClinicId, d.CreatedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(d => d.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // Treatments link to a diagnosis of the same consultation through this key.
        builder.HasAlternateKey(d => new { d.ClinicId, d.ConsultationId, d.Id });

        builder.HasIndex(d => d.ConsultationId)
            .HasDatabaseName(Diagnosis.OnePrimaryIndex)
            .IsUnique()
            .HasFilter("is_primary");

        // The patient's problem list: by patient, newest first.
        builder.HasIndex(d => new { d.ClinicId, d.PatientId, d.CreatedAt });
    }
}

internal sealed class TreatmentConfiguration : IEntityTypeConfiguration<Treatment>
{
    public void Configure(EntityTypeBuilder<Treatment> builder)
    {
        builder.ToTable("treatments", table =>
        {
            table.HasCheckConstraint("ck_treatments_type", "type IN ('Procedure', 'Medication', 'Fluids', 'Other')");
            table.HasCheckConstraint("ck_treatments_name", "btrim(name) <> ''");
        });
        builder.Property(t => t.Name).HasMaxLength(Treatment.NameMaxLength);
        builder.Property(t => t.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.Dose).HasMaxLength(Treatment.DetailMaxLength);
        builder.Property(t => t.Route).HasMaxLength(Treatment.DetailMaxLength);
        builder.Property(t => t.Frequency).HasMaxLength(Treatment.DetailMaxLength);
        builder.Property(t => t.Duration).HasMaxLength(Treatment.DetailMaxLength);
        builder.Property(t => t.Notes).HasMaxLength(Treatment.NotesMaxLength);

        builder.HasOne<Consultation>().WithMany(c => c.Treatments)
            .HasForeignKey(t => new { t.ClinicId, t.PatientId, t.ConsultationId })
            .HasPrincipalKey(c => new { c.ClinicId, c.PatientId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The linked diagnosis must belong to the same consultation (and so the same clinic and patient).
        builder.HasOne<Diagnosis>().WithMany()
            .HasForeignKey(t => new { t.ClinicId, t.ConsultationId, t.DiagnosisId })
            .HasPrincipalKey(d => new { d.ClinicId, d.ConsultationId, d.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany()
            .HasForeignKey(t => new { t.ClinicId, t.CreatedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(t => t.ClinicId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => new { t.ClinicId, t.ConsultationId });
    }
}
