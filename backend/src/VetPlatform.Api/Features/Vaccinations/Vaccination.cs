using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Consultations;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.Vaccinations;

/// <summary>
/// A vaccine dose given to a patient, here or elsewhere (docs/DATABASE.md section 5, ADR-014). A patient-level record
/// with an optional link to a consultation of the same patient. Never changed once saved: a wrong record is voided with
/// a reason and recorded again, so certificates and lot numbers always keep their trail (a database trigger enforces it).
/// </summary>
public sealed class Vaccination : Entity, IClinicOwned
{
    public const int VaccineNameMaxLength = 200;
    public const int ManufacturerMaxLength = 100;
    public const int LotNumberMaxLength = 50;
    public const int AdministeredElsewhereMaxLength = 200;
    public const int NotesMaxLength = 2000;
    public const int ReasonMaxLength = 500;

    public Guid ClinicId { get; set; }

    public Guid PatientId { get; set; }

    public Patient? Patient { get; set; }

    /// <summary>Optional: an in-progress or completed consultation of the same patient.</summary>
    public Guid? ConsultationId { get; set; }

    /// <summary>Optional catalogue vaccine (a Vaccine product, active when linked; B-20). Linking never changes stock.</summary>
    public Guid? ProductId { get; set; }

    /// <summary>Free text until the inventory product catalogue exists (B-19).</summary>
    public required string VaccineName { get; set; }

    public string? Manufacturer { get; set; }

    public string? LotNumber { get; set; }

    public DateOnly? LotExpiresOn { get; set; }

    /// <summary>The clinic-local day the dose was given.</summary>
    public DateOnly AdministeredOn { get; set; }

    /// <summary>
    /// The start of <see cref="AdministeredOn"/> in the clinic's time zone when recorded: the instant the patient
    /// history orders by. Not part of the API.
    /// </summary>
    public DateTimeOffset AdministeredDayStartsAt { get; set; }

    /// <summary>When the next dose is due; drives the clinic's due list.</summary>
    public DateOnly? NextDueOn { get; set; }

    /// <summary>Where the dose was given when not here (e.g. the previous clinic); null for doses given here.</summary>
    public string? AdministeredElsewhere { get; set; }

    /// <summary>The veterinarian who gave or supervised it; required exactly for doses given here.</summary>
    public Guid? VeterinarianId { get; set; }

    public User? Veterinarian { get; set; }

    public string? Notes { get; set; }

    public VaccinationStatus Status { get; set; } = VaccinationStatus.Active;

    public DateTimeOffset? VoidedAt { get; set; }

    public Guid? VoidedByUserId { get; set; }

    public User? VoidedBy { get; set; }

    public string? VoidReason { get; set; }

    public Guid? RecordedByUserId { get; set; }

    public User? RecordedBy { get; set; }
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum VaccinationStatus
{
    Active,

    /// <summary>Entered in error: kept for the record, left out of due dates.</summary>
    Voided,
}

internal sealed class VaccinationConfiguration : IEntityTypeConfiguration<Vaccination>
{
    public void Configure(EntityTypeBuilder<Vaccination> builder)
    {
        builder.ToTable("vaccinations", table =>
        {
            table.HasCheckConstraint("ck_vaccinations_status", "status IN ('Active', 'Voided')");
            table.HasCheckConstraint(
                "ck_vaccinations_lifecycle",
                "CASE status " +
                "WHEN 'Active' THEN voided_at IS NULL AND voided_by_user_id IS NULL AND void_reason IS NULL " +
                "WHEN 'Voided' THEN voided_at IS NOT NULL AND voided_by_user_id IS NOT NULL " +
                "AND void_reason IS NOT NULL AND btrim(void_reason) <> '' " +
                "ELSE false END");
            table.HasCheckConstraint("ck_vaccinations_vaccine_name", "btrim(vaccine_name) <> ''");
            // Given here: a veterinarian; given elsewhere: where, and no veterinarian of this clinic.
            table.HasCheckConstraint(
                "ck_vaccinations_given_by",
                "(administered_elsewhere IS NULL AND veterinarian_id IS NOT NULL) " +
                "OR (administered_elsewhere IS NOT NULL AND btrim(administered_elsewhere) <> '' AND veterinarian_id IS NULL)");
            table.HasCheckConstraint(
                "ck_vaccinations_dates",
                "administered_on BETWEEN DATE '2000-01-01' AND DATE '2100-12-31' " +
                "AND (next_due_on IS NULL OR (next_due_on > administered_on AND next_due_on <= DATE '2100-12-31')) " +
                "AND (lot_expires_on IS NULL OR lot_expires_on BETWEEN DATE '2000-01-01' AND DATE '2100-12-31')");
        });
        builder.Property(v => v.VaccineName).HasMaxLength(Vaccination.VaccineNameMaxLength);
        builder.Property(v => v.Manufacturer).HasMaxLength(Vaccination.ManufacturerMaxLength);
        builder.Property(v => v.LotNumber).HasMaxLength(Vaccination.LotNumberMaxLength);
        builder.Property(v => v.AdministeredElsewhere).HasMaxLength(Vaccination.AdministeredElsewhereMaxLength);
        builder.Property(v => v.Notes).HasMaxLength(Vaccination.NotesMaxLength);
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(v => v.VoidReason).HasMaxLength(Vaccination.ReasonMaxLength);

        // Every reference stays inside the clinic (ADR-005). The patient has its own key because the consultation
        // link is nullable, and a composite key with a null part is not checked (ADR-012).
        builder.HasOne(v => v.Patient).WithMany()
            .HasForeignKey(v => new { v.ClinicId, v.PatientId })
            .HasPrincipalKey(p => new { p.ClinicId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Consultation>().WithMany()
            .HasForeignKey(v => new { v.ClinicId, v.PatientId, v.ConsultationId })
            .HasPrincipalKey(c => new { c.ClinicId, c.PatientId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(v => v.Veterinarian).WithMany()
            .HasForeignKey(v => new { v.ClinicId, v.VeterinarianId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(v => v.VoidedBy).WithMany()
            .HasForeignKey(v => new { v.ClinicId, v.VoidedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(v => v.RecordedBy).WithMany()
            .HasForeignKey(v => new { v.ClinicId, v.RecordedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Inventory.Product>().WithMany()
            .HasForeignKey(v => new { v.ClinicId, v.ProductId })
            .HasPrincipalKey(p => new { p.ClinicId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(v => v.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // A patient's vaccinations, newest first (list and history); also serves the patient foreign key.
        builder.HasIndex(v => new { v.ClinicId, v.PatientId, v.AdministeredOn });
    }
}
