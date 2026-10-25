using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Consultations;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.ClinicalNotes;

/// <summary>
/// Clinical notes (docs/API.md section 9, ADR-015): append-only, so there is no edit and no delete. Every read and
/// write is clinic-filtered and audited; audit metadata names records, never note text.
/// </summary>
public sealed class ClinicalNoteService(
    AppDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor)
{
    public async Task<PagedResponse<ClinicalNoteResponse>> ListForPatientAsync(Guid patientId, PageQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId, cancellationToken))
        {
            throw NotFound("The patient was not found.");
        }

        var zone = await ZoneAsync(cancellationToken);
        var page = await db.ClinicalNotes.AsNoTracking().Include(n => n.Author)
            .Where(n => n.PatientId == patientId)
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .ToPageAsync(query, n => ClinicalNoteResponse.From(n, zone), cancellationToken);

        audit.Record(AuditActions.ClinicalNotesListed, nameof(Patient), patientId, new { noteIds = page.Data.Select(n => n.Id).ToList() });
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    public async Task<ClinicalNoteResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var note = await db.ClinicalNotes.AsNoTracking().Include(n => n.Author).SingleOrDefaultAsync(n => n.Id == id, cancellationToken)
            ?? throw NotFound("The clinical note was not found.");
        audit.Record(AuditActions.ClinicalNoteViewed, nameof(ClinicalNote), id, new { patientId = note.PatientId });
        await db.SaveChangesAsync(cancellationToken);
        return ClinicalNoteResponse.From(note, zone);
    }

    /// <summary>Notes may be added to a patient of any status: records of inactive or deceased animals still grow.</summary>
    public async Task<ClinicalNoteResponse> AddAsync(Guid patientId, AddClinicalNoteRequest request, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        if (!await db.Patients.AnyAsync(p => p.Id == patientId, cancellationToken))
        {
            throw NotFound("The patient was not found.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (request.ConsultationId is { } consultationId
            && !await ConsultationLinks.IsLinkableAsync(db, consultationId, patientId, cancellationToken))
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.",
                [new ApiErrorDetail("consultationId", ConsultationLinks.Message)]);
        }

        var note = new ClinicalNote
        {
            ClinicId = CurrentClinicId,
            PatientId = patientId,
            ConsultationId = request.ConsultationId,
            AuthorUserId = CallerId,
            Author = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken),
            Text = request.Text!.Trim(),
            CreatedAt = TimestampsInterceptor.ToDatabasePrecision(timeProvider.GetUtcNow()),
        };
        db.ClinicalNotes.Add(note);
        audit.Record(
            AuditActions.ClinicalNoteAdded, nameof(ClinicalNote), note.Id,
            new { patientId, consultationId = note.ConsultationId });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ClinicalNoteResponse.From(note, zone);
    }

    private Guid CurrentClinicId =>
        tenant.ClinicId ?? throw new InvalidOperationException("Clinical notes require a current clinic.");

    private Guid CallerId =>
        AuthClaims.GetUserId(httpContextAccessor.HttpContext!.User)
        ?? throw new InvalidOperationException("Clinical notes require a signed-in user.");

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) =>
        ClinicTimeZone.Of(await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken));

    private static ApiException NotFound(string message) => new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);
}
