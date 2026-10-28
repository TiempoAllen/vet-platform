using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Clients;
using VetPlatform.Api.Features.Consultations;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.Attachments;

/// <summary>
/// An uploaded file (image or PDF) owned by exactly one patient, consultation or client (docs/DATABASE.md section 6,
/// ADR-015). The content lives in private storage under <see cref="StorageKey"/>; this row holds its metadata. Never
/// changed once saved: a mistaken upload is withdrawn with a reason (a database trigger enforces it).
/// </summary>
public sealed class Attachment : Entity, IClinicOwned
{
    public const long MaxSizeBytes = 20L * 1024 * 1024;
    public const int FileNameMaxLength = 255;
    public const int DescriptionMaxLength = 500;
    public const int ReasonMaxLength = 500;

    /// <summary>The only content types accepted, recognised from the file's leading bytes (not its name).</summary>
    public static readonly IReadOnlyList<string> ContentTypes = ["image/jpeg", "image/png", "image/webp", "application/pdf"];

    public Guid ClinicId { get; set; }

    /// <summary>Set for patient and consultation files (a consultation file also names the consultation's patient).</summary>
    public Guid? PatientId { get; set; }

    public Guid? ConsultationId { get; set; }

    /// <summary>Set only for client files.</summary>
    public Guid? ClientId { get; set; }

    public AttachmentOwnerType OwnerType =>
        ConsultationId is not null ? AttachmentOwnerType.Consultation
        : PatientId is not null ? AttachmentOwnerType.Patient
        : AttachmentOwnerType.Client;

    /// <summary>Random key of the content in the file store; never shown to clients.</summary>
    public required string StorageKey { get; set; }

    /// <summary>The uploader's file name, sanitised; used only as the download name.</summary>
    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Lowercase hex SHA-256 of the content, for integrity checks.</summary>
    public required string Sha256 { get; set; }

    public string? Description { get; set; }

    public AttachmentStatus Status { get; set; } = AttachmentStatus.Active;

    public DateTimeOffset? WithdrawnAt { get; set; }

    public Guid? WithdrawnByUserId { get; set; }

    public User? WithdrawnBy { get; set; }

    public string? WithdrawReason { get; set; }

    public Guid? UploadedByUserId { get; set; }

    public User? UploadedBy { get; set; }
}

public enum AttachmentOwnerType
{
    Patient,
    Consultation,
    Client,
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum AttachmentStatus
{
    Active,

    /// <summary>Uploaded by mistake: hidden from lists and not downloadable; the row and the content stay.</summary>
    Withdrawn,
}

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("attachments", table =>
        {
            table.HasCheckConstraint("ck_attachments_status", "status IN ('Active', 'Withdrawn')");
            table.HasCheckConstraint(
                "ck_attachments_lifecycle",
                "CASE status " +
                "WHEN 'Active' THEN withdrawn_at IS NULL AND withdrawn_by_user_id IS NULL AND withdraw_reason IS NULL " +
                "WHEN 'Withdrawn' THEN withdrawn_at IS NOT NULL AND withdrawn_by_user_id IS NOT NULL " +
                "AND withdraw_reason IS NOT NULL AND btrim(withdraw_reason) <> '' " +
                "ELSE false END");
            // Exactly one owner: a client, or a patient (with or without one of its consultations).
            table.HasCheckConstraint(
                "ck_attachments_owner",
                "(client_id IS NOT NULL AND patient_id IS NULL AND consultation_id IS NULL) " +
                "OR (client_id IS NULL AND patient_id IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_attachments_content_type", $"content_type IN ({string.Join(", ", Attachment.ContentTypes.Select(t => $"'{t}'"))})");
            table.HasCheckConstraint("ck_attachments_size_bytes", $"size_bytes BETWEEN 1 AND {Attachment.MaxSizeBytes}");
            table.HasCheckConstraint("ck_attachments_storage_key", "storage_key ~ '^[0-9a-f]{32}$'");
            table.HasCheckConstraint("ck_attachments_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("ck_attachments_file_name", "btrim(file_name) <> ''");
        });
        builder.Property(a => a.StorageKey).HasMaxLength(32).IsFixedLength();
        builder.Property(a => a.FileName).HasMaxLength(Attachment.FileNameMaxLength);
        builder.Property(a => a.ContentType).HasMaxLength(32);
        builder.Property(a => a.Sha256).HasMaxLength(64).IsFixedLength();
        builder.Property(a => a.Description).HasMaxLength(Attachment.DescriptionMaxLength);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.WithdrawReason).HasMaxLength(Attachment.ReasonMaxLength);
        builder.Ignore(a => a.OwnerType);

        // Every owner stays inside the clinic (ADR-005); a consultation file names the consultation's own patient.
        builder.HasOne<Patient>().WithMany()
            .HasForeignKey(a => new { a.ClinicId, a.PatientId })
            .HasPrincipalKey(p => new { p.ClinicId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Consultation>().WithMany()
            .HasForeignKey(a => new { a.ClinicId, a.PatientId, a.ConsultationId })
            .HasPrincipalKey(c => new { c.ClinicId, c.PatientId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Client>().WithMany()
            .HasForeignKey(a => new { a.ClinicId, a.ClientId })
            .HasPrincipalKey(c => new { c.ClinicId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(a => a.WithdrawnBy).WithMany()
            .HasForeignKey(a => new { a.ClinicId, a.WithdrawnByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(a => a.UploadedBy).WithMany()
            .HasForeignKey(a => new { a.ClinicId, a.UploadedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(a => a.ClinicId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => a.StorageKey).IsUnique();

        // A patient's (and its consultations') files and a client's files, newest first; these also serve the keys.
        builder.HasIndex(a => new { a.ClinicId, a.PatientId, a.CreatedAt });
        builder.HasIndex(a => new { a.ClinicId, a.ClientId, a.CreatedAt });
    }
}
