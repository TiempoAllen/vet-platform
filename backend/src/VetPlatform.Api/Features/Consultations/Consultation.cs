using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.Consultations;

/// <summary>
/// A clinical encounter with SOAP notes and vitals (docs/DATABASE.md section 5, ADR-011). Editable while in
/// progress; once completed the notes are frozen and corrections are append-only <see cref="ConsultationAddendum"/>s
/// (FR-CLN-2). Never deleted: one opened by mistake is discarded with a reason.
/// </summary>
public sealed class Consultation : Entity, IClinicOwned
{
    public const int NotesMaxLength = 10_000;
    public const int ReasonMaxLength = 500;

    /// <summary>Partial unique index: one in-progress consultation per patient.</summary>
    public const string OneOpenPerPatientIndex = "ux_consultations_one_open_per_patient";

    /// <summary>Partial unique index: one (non-discarded) consultation per appointment.</summary>
    public const string OnePerAppointmentIndex = "ux_consultations_one_per_appointment";

    public Guid ClinicId { get; set; }

    public Guid PatientId { get; set; }

    public Patient? Patient { get; set; }

    /// <summary>The checked-in appointment it was opened from, or null for a walk-in.</summary>
    public Guid? AppointmentId { get; set; }

    /// <summary>The veterinarian of record.</summary>
    public Guid VeterinarianId { get; set; }

    public User? Veterinarian { get; set; }

    public ConsultationStatus Status { get; set; } = ConsultationStatus.InProgress;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The veterinarian who signed the consultation off.</summary>
    public Guid? CompletedByUserId { get; set; }

    public User? CompletedBy { get; set; }

    public DateTimeOffset? DiscardedAt { get; set; }

    public string? DiscardReason { get; set; }

    public string? Subjective { get; set; }

    public string? Objective { get; set; }

    public string? Assessment { get; set; }

    public string? Plan { get; set; }

    public decimal? WeightKg { get; set; }

    public decimal? TemperatureC { get; set; }

    public int? HeartRate { get; set; }

    public int? RespiratoryRate { get; set; }

    /// <summary>1 (emaciated) to 9 (obese).</summary>
    public int? BodyConditionScore { get; set; }

    /// <summary>0 (none) to 10 (worst).</summary>
    public int? PainScore { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedBy { get; set; }

    public List<ConsultationAddendum> Addenda { get; set; } = [];
}

public enum ConsultationStatus
{
    InProgress,
    Completed,
    Discarded,
}

/// <summary>
/// A dated, signed correction or addition to a completed consultation. Append-only: database triggers reject UPDATE
/// and DELETE, so the clinical record keeps every word that was ever signed (FR-CLN-2).
/// </summary>
public sealed class ConsultationAddendum : IClinicOwned
{
    public const int TextMaxLength = 4000;

    /// <summary>Addenda are permanent, so their number is capped to keep every read of the record bounded.</summary>
    public const int MaxPerConsultation = 50;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ClinicId { get; set; }

    public Guid ConsultationId { get; set; }

    public Guid AuthorUserId { get; set; }

    public User? Author { get; set; }

    public required string Text { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Vital-sign limits; outside them the value is a typo, not a patient.</summary>
public static class VitalLimits
{
    public const decimal MaxWeightKg = Patient.MaxWeightKg;
    public const decimal MinTemperatureC = 25m;
    public const decimal MaxTemperatureC = 45m;
    public const int MaxHeartRate = 400;
    public const int MaxRespiratoryRate = 200;
}

internal sealed class ConsultationConfiguration : IEntityTypeConfiguration<Consultation>
{
    public void Configure(EntityTypeBuilder<Consultation> builder)
    {
        builder.ToTable("consultations", table =>
        {
            table.HasCheckConstraint("ck_consultations_status", "status IN ('InProgress', 'Completed', 'Discarded')");
            table.HasCheckConstraint(
                "ck_consultations_lifecycle",
                "CASE status " +
                "WHEN 'InProgress' THEN completed_at IS NULL AND completed_by_user_id IS NULL AND discarded_at IS NULL AND discard_reason IS NULL " +
                "WHEN 'Completed' THEN completed_at IS NOT NULL AND completed_by_user_id IS NOT NULL AND completed_at >= started_at AND discarded_at IS NULL AND discard_reason IS NULL " +
                "WHEN 'Discarded' THEN completed_at IS NULL AND completed_by_user_id IS NULL AND discarded_at IS NOT NULL " +
                "AND discarded_at >= started_at AND discard_reason IS NOT NULL AND btrim(discard_reason) <> '' " +
                "ELSE false END");
            table.HasCheckConstraint("ck_consultations_weight_kg", $"weight_kg > 0 AND weight_kg <= {VitalLimits.MaxWeightKg}");
            table.HasCheckConstraint(
                "ck_consultations_temperature_c",
                $"temperature_c BETWEEN {VitalLimits.MinTemperatureC} AND {VitalLimits.MaxTemperatureC}");
            table.HasCheckConstraint("ck_consultations_heart_rate", $"heart_rate BETWEEN 1 AND {VitalLimits.MaxHeartRate}");
            table.HasCheckConstraint(
                "ck_consultations_respiratory_rate", $"respiratory_rate BETWEEN 1 AND {VitalLimits.MaxRespiratoryRate}");
            table.HasCheckConstraint("ck_consultations_body_condition_score", "body_condition_score BETWEEN 1 AND 9");
            table.HasCheckConstraint("ck_consultations_pain_score", "pain_score BETWEEN 0 AND 10");
        });
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.DiscardReason).HasMaxLength(Consultation.ReasonMaxLength);
        builder.Property(c => c.Subjective).HasMaxLength(Consultation.NotesMaxLength);
        builder.Property(c => c.Objective).HasMaxLength(Consultation.NotesMaxLength);
        builder.Property(c => c.Assessment).HasMaxLength(Consultation.NotesMaxLength);
        builder.Property(c => c.Plan).HasMaxLength(Consultation.NotesMaxLength);
        builder.Property(c => c.WeightKg).HasPrecision(7, 3);
        builder.Property(c => c.TemperatureC).HasPrecision(4, 1);

        // Every reference stays inside the clinic (ADR-005); nothing here is ever deleted.
        builder.HasOne(c => c.Patient).WithMany()
            .HasForeignKey(c => new { c.ClinicId, c.PatientId })
            .HasPrincipalKey(p => new { p.ClinicId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Appointment>().WithMany()
            .HasForeignKey(c => new { c.ClinicId, c.AppointmentId })
            .HasPrincipalKey(a => new { a.ClinicId, a.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.Veterinarian).WithMany()
            .HasForeignKey(c => new { c.ClinicId, c.VeterinarianId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.CompletedBy).WithMany()
            .HasForeignKey(c => new { c.ClinicId, c.CompletedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.CreatedBy).WithMany()
            .HasForeignKey(c => new { c.ClinicId, c.CreatedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(c => c.ClinicId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(c => c.Addenda).WithOne()
            .HasForeignKey(a => new { a.ClinicId, a.ConsultationId })
            .HasPrincipalKey(c => new { c.ClinicId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Unique (clinic_id, id): addenda (above) and later diagnoses, treatments and prescriptions use it (ADR-005).
        builder.HasAlternateKey(c => new { c.ClinicId, c.Id });

        builder.HasIndex(c => new { c.ClinicId, c.PatientId })
            .HasDatabaseName(Consultation.OneOpenPerPatientIndex)
            .IsUnique()
            .HasFilter("status = 'InProgress'");
        builder.HasIndex(c => new { c.ClinicId, c.AppointmentId })
            .HasDatabaseName(Consultation.OnePerAppointmentIndex)
            .IsUnique()
            .HasFilter("appointment_id IS NOT NULL AND status <> 'Discarded'");

        // A patient's consultations, newest first (list and history).
        builder.HasIndex(c => new { c.ClinicId, c.PatientId, c.StartedAt });
    }
}

internal sealed class ConsultationAddendumConfiguration : IEntityTypeConfiguration<ConsultationAddendum>
{
    public void Configure(EntityTypeBuilder<ConsultationAddendum> builder)
    {
        builder.ToTable("consultation_addenda", table =>
            table.HasCheckConstraint("ck_consultation_addenda_text", "btrim(text) <> ''"));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Text).HasMaxLength(ConsultationAddendum.TextMaxLength);
        builder.HasOne(a => a.Author).WithMany()
            .HasForeignKey(a => new { a.ClinicId, a.AuthorUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(a => a.ClinicId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.ClinicId, a.ConsultationId, a.CreatedAt });
    }
}
