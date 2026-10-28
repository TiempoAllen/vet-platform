using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Consultations;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.ClinicalNotes;

/// <summary>
/// A dated, signed text note on a patient outside the SOAP notes: phone advice, a lab result received, an owner update
/// (docs/DATABASE.md section 5, ADR-015). Optionally linked to a consultation of the same patient. Append-only like
/// consultation addenda: database triggers reject UPDATE and DELETE, and a correction is a new note.
/// </summary>
public sealed class ClinicalNote : IClinicOwned
{
    public const int TextMaxLength = 4000;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ClinicId { get; set; }

    public Guid PatientId { get; set; }

    /// <summary>Optional: an in-progress or completed consultation of the same patient.</summary>
    public Guid? ConsultationId { get; set; }

    public Guid AuthorUserId { get; set; }

    public User? Author { get; set; }

    public required string Text { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ClinicalNoteConfiguration : IEntityTypeConfiguration<ClinicalNote>
{
    public void Configure(EntityTypeBuilder<ClinicalNote> builder)
    {
        builder.ToTable("clinical_notes", table => table.HasCheckConstraint("ck_clinical_notes_text", "btrim(text) <> ''"));
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Text).HasMaxLength(ClinicalNote.TextMaxLength);

        // The patient has its own key because the consultation link is nullable (ADR-012).
        builder.HasOne<Patient>().WithMany()
            .HasForeignKey(n => new { n.ClinicId, n.PatientId })
            .HasPrincipalKey(p => new { p.ClinicId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Consultation>().WithMany()
            .HasForeignKey(n => new { n.ClinicId, n.PatientId, n.ConsultationId })
            .HasPrincipalKey(c => new { c.ClinicId, c.PatientId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(n => n.Author).WithMany()
            .HasForeignKey(n => new { n.ClinicId, n.AuthorUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(n => n.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // A patient's notes, newest first (list and history); also serves the patient key.
        builder.HasIndex(n => new { n.ClinicId, n.PatientId, n.CreatedAt });
    }
}
