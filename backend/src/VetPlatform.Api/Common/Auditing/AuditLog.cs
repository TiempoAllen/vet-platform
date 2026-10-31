using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Common.Auditing;

/// <summary>
/// An append-only record of a security or business event (docs/DATABASE.md section 10, SECURITY.md section 11).
/// A database trigger rejects UPDATE and DELETE. Metadata must never contain secrets, passwords, tokens or more
/// personal data than the event needs.
/// </summary>
public sealed class AuditLog
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>When the event happened, from the application clock.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// When the row was written, set by the database (a trigger overrides whatever is sent), so the stored timeline
    /// cannot be back- or forward-dated by the application.
    /// </summary>
    public DateTimeOffset RecordedAt { get; init; }

    /// <summary>Null only for events with no clinic, such as a sign-in attempt for an unknown email.</summary>
    public Guid? ClinicId { get; init; }

    public Guid? ActorUserId { get; init; }

    public required string Action { get; init; }

    public string? EntityType { get; init; }

    public Guid? EntityId { get; init; }

    /// <summary>JSON object (jsonb).</summary>
    public string? Metadata { get; init; }

    public string? RequestId { get; init; }
}

/// <summary>Stable action names. Clients and reports may filter on them; do not rename.</summary>
public static class AuditActions
{
    public const string LoginSucceeded = "auth.login_succeeded";
    public const string LoginFailed = "auth.login_failed";
    public const string AccountLocked = "auth.account_locked";
    public const string Logout = "auth.logout";
    public const string PasswordChanged = "auth.password_changed";
    public const string PasswordChangeFailed = "auth.password_change_failed";
    public const string SessionsRevoked = "auth.sessions_revoked";
    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserRolesChanged = "user.roles_changed";
    public const string ClientCreated = "client.created";
    public const string ClientUpdated = "client.updated";
    public const string PatientCreated = "patient.created";
    public const string PatientUpdated = "patient.updated";
    public const string ClinicUpdated = "clinic.updated";
    public const string WorkingHoursCreated = "working_hours.created";
    public const string WorkingHoursUpdated = "working_hours.updated";
    public const string WorkingHoursDeleted = "working_hours.deleted";
    public const string ScheduleExceptionCreated = "schedule_exception.created";
    public const string ScheduleExceptionUpdated = "schedule_exception.updated";
    public const string ScheduleExceptionDeleted = "schedule_exception.deleted";
    public const string AppointmentCreated = "appointment.created";
    public const string AppointmentUpdated = "appointment.updated";
    public const string AppointmentCheckedIn = "appointment.checked_in";
    public const string AppointmentCheckInUndone = "appointment.check_in_undone";
    public const string AppointmentCompleted = "appointment.completed";
    public const string AppointmentCancelled = "appointment.cancelled";
    public const string AppointmentNoShow = "appointment.no_show";
    public const string AppointmentNoShowUndone = "appointment.no_show_undone";
    public const string ConsultationCreated = "consultation.created";
    public const string ConsultationUpdated = "consultation.updated";
    public const string ConsultationCompleted = "consultation.completed";
    public const string ConsultationDiscarded = "consultation.discarded";
    public const string ConsultationAddendumAdded = "consultation.addendum_added";
    public const string DiagnosisAdded = "diagnosis.added";
    public const string DiagnosisUpdated = "diagnosis.updated";
    public const string DiagnosisRemoved = "diagnosis.removed";
    public const string TreatmentAdded = "treatment.added";
    public const string TreatmentUpdated = "treatment.updated";
    public const string TreatmentRemoved = "treatment.removed";
    public const string PrescriptionAdded = "prescription.added";
    public const string PrescriptionUpdated = "prescription.updated";
    public const string PrescriptionRemoved = "prescription.removed";
    public const string PrescriptionCancelled = "prescription.cancelled";
    public const string VaccinationRecorded = "vaccination.recorded";
    public const string VaccinationVoided = "vaccination.voided";
    public const string ClinicalNoteAdded = "clinical_note.added";
    public const string AttachmentUploaded = "attachment.uploaded";
    public const string AttachmentWithdrawn = "attachment.withdrawn";
    public const string TaxRateCreated = "tax_rate.created";
    public const string TaxRateUpdated = "tax_rate.updated";
    public const string InvoiceCreated = "invoice.created";
    public const string InvoiceUpdated = "invoice.updated";
    public const string InvoiceDeleted = "invoice.deleted";
    public const string InvoiceItemAdded = "invoice.item_added";
    public const string InvoiceItemUpdated = "invoice.item_updated";
    public const string InvoiceItemRemoved = "invoice.item_removed";
    public const string InvoiceIssued = "invoice.issued";
    public const string InvoiceVoided = "invoice.voided";
    public const string PaymentRecorded = "payment.recorded";
    public const string PaymentRefunded = "payment.refunded";
    public const string ReceiptIssued = "receipt.issued";
    public const string ProductCreated = "product.created";
    public const string ProductUpdated = "product.updated";
    public const string StockReceived = "stock.received";
    public const string StockDispensed = "stock.dispensed";
    public const string StockSold = "stock.sold";
    public const string StockAdjusted = "stock.adjusted";
    public const string StockReturnedToSupplier = "stock.returned_to_supplier";
    public const string StockReturnedFromClient = "stock.returned_from_client";
    public const string StockExpired = "stock.expired";
    public const string StockReversed = "stock.reversed";

    /// <summary>Reads of clinical records (SECURITY.md section 10).</summary>
    public const string ConsultationViewed = "consultation.viewed";
    public const string ConsultationsListed = "consultation.listed";
    public const string PatientHistoryViewed = "patient.history_viewed";
    public const string DiagnosesListed = "diagnosis.listed";
    public const string PrescriptionsListed = "prescription.listed";
    public const string VaccinationViewed = "vaccination.viewed";
    public const string VaccinationsListed = "vaccination.listed";

    /// <summary>The clinic-wide due list (owner contact details, vaccine names and due dates).</summary>
    public const string VaccinationsDueListed = "vaccination.due_listed";
    public const string ClinicalNoteViewed = "clinical_note.viewed";
    public const string ClinicalNotesListed = "clinical_note.listed";

    /// <summary>Reads of attachments (any owner): metadata, lists and content downloads.</summary>
    public const string AttachmentViewed = "attachment.viewed";
    public const string AttachmentsListed = "attachment.listed";
    public const string AttachmentDownloaded = "attachment.downloaded";

    /// <summary>A receipt opened (viewed or reprinted).</summary>
    public const string ReceiptViewed = "receipt.viewed";
    public const string ReceiptsListed = "receipt.listed";
}

/// <summary>
/// Adds audit entries to the current unit of work; they are written by the caller's next <c>SaveChanges</c>, so an
/// entry commits atomically with the change it describes. The request id always comes from the current request.
/// </summary>
public sealed class AuditTrail(Persistence.AppDbContext db, IHttpContextAccessor httpContextAccessor, TimeProvider timeProvider)
{
    /// <summary>camelCase, with enums written by name ("Disabled", "VeterinarianDoubleBooked"), never as numbers.</summary>
    private static readonly JsonSerializerOptions MetadataOptions = new(JsonSerializerOptions.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Records an action by the signed-in caller, in the caller's clinic.</summary>
    public void Record(string action, string? entityType = null, Guid? entityId = null, object? metadata = null)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        var signedIn = principal?.Identity?.IsAuthenticated == true;
        RecordFor(
            action, entityType, entityId, metadata,
            actorUserId: signedIn ? AuthClaims.GetUserId(principal!) : null,
            clinicId: signedIn ? AuthClaims.GetClinicId(principal!) : null);
    }

    /// <summary>
    /// Records an action with an explicit actor and clinic, used exactly as given (null stays null). For events whose
    /// subject is not the caller: sign-in attempts (the browser may be signed in as someone else) and the CLI.
    /// </summary>
    public void RecordFor(
        string action,
        string? entityType,
        Guid? entityId,
        object? metadata,
        Guid? actorUserId,
        Guid? clinicId)
    {
        db.AuditLogs.Add(new AuditLog
        {
            OccurredAt = timeProvider.GetUtcNow(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            ActorUserId = actorUserId,
            ClinicId = clinicId,
            Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata, MetadataOptions),
            RequestId = httpContextAccessor.HttpContext?.TraceIdentifier,
        });
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.Property(log => log.Action).HasMaxLength(100);
        builder.Property(log => log.EntityType).HasMaxLength(100);
        builder.Property(log => log.Metadata).HasColumnType("jsonb");
        builder.Property(log => log.RequestId).HasMaxLength(64);
        builder.Property(log => log.RecordedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        // Audit rows must not dangle, and their clinic and actor can never be deleted out from under them.
        builder.HasOne<Clinic>().WithMany().HasForeignKey(log => log.ClinicId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(log => log.ActorUserId).OnDelete(DeleteBehavior.Restrict);

        // Matches the expected B-27 queries: a clinic's log newest first, and the history of one entity.
        builder.HasIndex(log => new { log.ClinicId, log.OccurredAt });
        builder.HasIndex(log => new { log.EntityType, log.EntityId });
    }
}
