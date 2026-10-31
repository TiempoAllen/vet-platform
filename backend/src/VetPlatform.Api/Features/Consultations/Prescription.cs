using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Consultations;

/// <summary>
/// Take-home medication prescribed in a consultation (docs/DATABASE.md section 5, ADR-013). Only veterinarians
/// (<c>prescriptions.issue</c>) write them. Editable while the consultation is in progress; after completion it is
/// frozen and can only be cancelled, with a reason, so the record keeps what was prescribed.
/// </summary>
public sealed class Prescription : Entity, IClinicOwned
{
    public const int MedicationNameMaxLength = 200;
    public const int DetailMaxLength = 100;
    public const int QuantityUnitMaxLength = 50;
    public const int InstructionsMaxLength = 2000;
    public const int ReasonMaxLength = 500;
    public const int MaxPerConsultation = 50;
    public const int MaxRefills = 12;

    /// <summary>Upper bound for <see cref="Quantity"/>; beyond it the value is a typo.</summary>
    public const decimal MaxQuantity = 100_000m;

    public Guid ClinicId { get; set; }

    public Guid ConsultationId { get; set; }

    /// <summary>The consultation's patient; the composite foreign key makes the database guarantee they match.</summary>
    public Guid PatientId { get; set; }

    /// <summary>Optional: the diagnosis of the same consultation this treats. Removing that diagnosis unlinks it.</summary>
    public Guid? DiagnosisId { get; set; }

    /// <summary>Optional catalogue medication (a Medication product, active when linked; B-20). Linking never changes stock.</summary>
    public Guid? ProductId { get; set; }

    /// <summary>Free text until the inventory product catalogue exists (B-19).</summary>
    public required string MedicationName { get; set; }

    public required string Dose { get; set; }

    public string? Route { get; set; }

    public required string Frequency { get; set; }

    public string? Duration { get; set; }

    /// <summary>Amount to supply, in <see cref="QuantityUnit"/> (e.g. 14 tablets).</summary>
    public decimal Quantity { get; set; }

    public required string QuantityUnit { get; set; }

    /// <summary>Number of repeat supplies allowed; recorded only (refills are not tracked yet).</summary>
    public int Refills { get; set; }

    /// <summary>Directions for the owner.</summary>
    public string? Instructions { get; set; }

    public PrescriptionStatus Status { get; set; } = PrescriptionStatus.Active;

    /// <summary>The veterinarian who wrote it.</summary>
    public Guid PrescribedByUserId { get; set; }

    public User? PrescribedBy { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledByUserId { get; set; }

    public User? CancelledBy { get; set; }

    public string? CancelReason { get; set; }
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum PrescriptionStatus
{
    /// <summary>Prescribed and not cancelled (says nothing about whether the course is still running).</summary>
    Active,

    /// <summary>Stopped by a veterinarian after the consultation was completed.</summary>
    Cancelled,
}

internal sealed class PrescriptionConfiguration : IEntityTypeConfiguration<Prescription>
{
    public void Configure(EntityTypeBuilder<Prescription> builder)
    {
        builder.ToTable("prescriptions", table =>
        {
            table.HasCheckConstraint("ck_prescriptions_status", "status IN ('Active', 'Cancelled')");
            table.HasCheckConstraint(
                "ck_prescriptions_lifecycle",
                "CASE status " +
                "WHEN 'Active' THEN cancelled_at IS NULL AND cancelled_by_user_id IS NULL AND cancel_reason IS NULL " +
                "WHEN 'Cancelled' THEN cancelled_at IS NOT NULL AND cancelled_by_user_id IS NOT NULL " +
                "AND cancel_reason IS NOT NULL AND btrim(cancel_reason) <> '' " +
                "ELSE false END");
            table.HasCheckConstraint("ck_prescriptions_medication_name", "btrim(medication_name) <> ''");
            table.HasCheckConstraint("ck_prescriptions_dose", "btrim(dose) <> ''");
            table.HasCheckConstraint("ck_prescriptions_frequency", "btrim(frequency) <> ''");
            table.HasCheckConstraint("ck_prescriptions_quantity_unit", "btrim(quantity_unit) <> ''");
            table.HasCheckConstraint("ck_prescriptions_quantity", $"quantity > 0 AND quantity <= {Prescription.MaxQuantity}");
            table.HasCheckConstraint("ck_prescriptions_refills", $"refills BETWEEN 0 AND {Prescription.MaxRefills}");
        });
        builder.Property(p => p.MedicationName).HasMaxLength(Prescription.MedicationNameMaxLength);
        builder.Property(p => p.Dose).HasMaxLength(Prescription.DetailMaxLength);
        builder.Property(p => p.Route).HasMaxLength(Prescription.DetailMaxLength);
        builder.Property(p => p.Frequency).HasMaxLength(Prescription.DetailMaxLength);
        builder.Property(p => p.Duration).HasMaxLength(Prescription.DetailMaxLength);
        builder.Property(p => p.Quantity).HasPrecision(10, 3);
        builder.Property(p => p.QuantityUnit).HasMaxLength(Prescription.QuantityUnitMaxLength);
        builder.Property(p => p.Instructions).HasMaxLength(Prescription.InstructionsMaxLength);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.CancelReason).HasMaxLength(Prescription.ReasonMaxLength);

        // Same clinic, same consultation, same patient (ADR-012).
        builder.HasOne<Consultation>().WithMany(c => c.Prescriptions)
            .HasForeignKey(p => new { p.ClinicId, p.PatientId, p.ConsultationId })
            .HasPrincipalKey(c => new { c.ClinicId, c.PatientId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The linked diagnosis must belong to the same consultation.
        builder.HasOne<Diagnosis>().WithMany()
            .HasForeignKey(p => new { p.ClinicId, p.ConsultationId, p.DiagnosisId })
            .HasPrincipalKey(d => new { d.ClinicId, d.ConsultationId, d.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.PrescribedBy).WithMany()
            .HasForeignKey(p => new { p.ClinicId, p.PrescribedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.CancelledBy).WithMany()
            .HasForeignKey(p => new { p.ClinicId, p.CancelledByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Inventory.Product>().WithMany()
            .HasForeignKey(p => new { p.ClinicId, p.ProductId })
            .HasPrincipalKey(product => new { product.ClinicId, product.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(p => p.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // The consultation foreign key's index (clinic_id, patient_id, consultation_id) serves the medication list.
    }
}
