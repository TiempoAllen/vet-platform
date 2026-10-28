using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Storage;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Clients;
using VetPlatform.Api.Features.Consultations;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.Attachments;

/// <summary>
/// Attachments (docs/API.md section 9, ADR-015). Access follows the owner: client files need <c>clients.read</c> /
/// <c>clients.manage</c>, patient and consultation files <c>clinical.read</c> / <c>clinical.write</c>. Every read,
/// download and change is clinic-filtered and audited; audit metadata never holds file names or descriptions.
/// </summary>
public sealed class AttachmentService(
    AppDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IFileStore files,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AttachmentService> logger)
{
    // ---------- Upload ----------

    /// <summary>
    /// Stores a file for an owner already resolved by <see cref="ResolveOwnerAsync"/> (before the body was read). Then
    /// the form (422), the type (415) and the size (413) are checked. The content is written before the row; if the row
    /// cannot be saved the content is removed again, unless the commit's outcome is unknown (an orphan file is
    /// harmless, a row without content is not).
    /// </summary>
    public async Task<AttachmentResponse> UploadAsync(
        AttachmentOwner owner,
        AttachmentUpload upload,
        CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var (ownerType, patientId, consultationId, clientId) = owner;

        var file = CheckForm(upload);
        var header = new byte[FileSignatures.HeaderLength];
        int read;
        await using (var head = file.OpenReadStream())
        {
            read = await head.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        }

        var contentType = FileSignatures.Detect(header.AsSpan(0, read))
            ?? throw new ApiException(
                StatusCodes.Status415UnsupportedMediaType, ErrorCodes.UnsupportedMediaType,
                "Only JPEG, PNG, WebP images and PDF documents can be attached.");
        if (file.Length > Attachment.MaxSizeBytes)
        {
            throw TooLarge();
        }

        string sha256;
        await using (var content = file.OpenReadStream())
        {
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));
        }

        var key = StorageKeys.New();
        await using (var content = file.OpenReadStream())
        {
            await files.SaveAsync(key, content, cancellationToken);
        }

        var committing = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (consultationId is { } linked && !await ConsultationLinks.IsLinkableAsync(db, linked, patientId!.Value, cancellationToken))
            {
                throw Conflict("The consultation was discarded; files cannot be attached to it.");
            }

            var attachment = new Attachment
            {
                ClinicId = CurrentClinicId,
                PatientId = patientId,
                ConsultationId = consultationId,
                ClientId = clientId,
                StorageKey = key,
                FileName = FileSignatures.SafeFileName(file.FileName, contentType),
                ContentType = contentType,
                SizeBytes = file.Length,
                Sha256 = sha256,
                Description = NullIfBlank(upload.Description),
                UploadedByUserId = CallerId,
                UploadedBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken),
            };
            db.Attachments.Add(attachment);
            audit.Record(
                AuditActions.AttachmentUploaded, nameof(Attachment), attachment.Id,
                new { ownerType, patientId, consultationId, clientId, contentType, sizeBytes = file.Length });
            await db.SaveChangesAsync(cancellationToken);
            committing = true;
            await transaction.CommitAsync(cancellationToken);
            return AttachmentResponse.From(attachment, zone);
        }
        catch when (committing)
        {
            // The commit may have reached the database before the connection failed: keep the content.
            logger.LogWarning("The commit of an attachment failed with an unknown outcome; its content is kept.");
            throw;
        }
        catch
        {
            await RemoveContentAsync(key);
            throw;
        }
    }

    // ---------- Reads ----------

    /// <summary>A patient's files, including those of its consultations; newest first. Audited.</summary>
    public Task<PagedResponse<AttachmentResponse>> ListForPatientAsync(Guid patientId, AttachmentListQuery query, CancellationToken cancellationToken) =>
        ListAsync(
            nameof(Patient), patientId,
            () => db.Patients.AnyAsync(p => p.Id == patientId, cancellationToken),
            a => a.PatientId == patientId, query, cancellationToken);

    /// <summary>
    /// Filters on the consultation's patient as well, so the (clinic_id, patient_id, ...) indexes serve the query
    /// instead of a scan of the clinic's files.
    /// </summary>
    public async Task<PagedResponse<AttachmentResponse>> ListForConsultationAsync(
        Guid consultationId,
        AttachmentListQuery query,
        CancellationToken cancellationToken)
    {
        var patientId = await db.Consultations.Where(c => c.Id == consultationId).Select(c => (Guid?)c.PatientId)
            .SingleOrDefaultAsync(cancellationToken);
        return await ListAsync(
            nameof(Consultation), consultationId,
            () => Task.FromResult(patientId is not null),
            a => a.PatientId == patientId && a.ConsultationId == consultationId, query, cancellationToken);
    }

    public Task<PagedResponse<AttachmentResponse>> ListForClientAsync(Guid clientId, AttachmentListQuery query, CancellationToken cancellationToken) =>
        ListAsync(
            nameof(Client), clientId,
            () => db.Clients.AnyAsync(c => c.Id == clientId, cancellationToken),
            a => a.ClientId == clientId, query, cancellationToken);

    public async Task<AttachmentResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var attachment = await WithPeople(db.Attachments.AsNoTracking()).SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw NotFound();
        RequireAccess(attachment, write: false);
        audit.Record(AuditActions.AttachmentViewed, nameof(Attachment), id, new { ownerType = attachment.OwnerType });
        await db.SaveChangesAsync(cancellationToken);
        return AttachmentResponse.From(attachment, zone);
    }

    /// <summary>The content of an active attachment; the caller disposes the stream. Audited before it is returned.</summary>
    public async Task<(Stream Content, string ContentType, string FileName)> DownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw NotFound();
        RequireAccess(attachment, write: false);
        if (attachment.Status == AttachmentStatus.Withdrawn)
        {
            throw Conflict("The attachment was withdrawn and can no longer be downloaded.");
        }

        var content = await files.OpenReadAsync(attachment.StorageKey, cancellationToken)
            ?? throw new InvalidOperationException($"The content of attachment {id} is missing from the file store.");
        try
        {
            audit.Record(AuditActions.AttachmentDownloaded, nameof(Attachment), id, new { ownerType = attachment.OwnerType });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }

        return (content, attachment.ContentType, attachment.FileName);
    }

    // ---------- Withdraw ----------

    /// <summary>Hides a mistaken upload. The row and the stored content stay, with who withdrew it, when and why.</summary>
    public async Task<AttachmentResponse> WithdrawAsync(Guid id, WithdrawAttachmentRequest request, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Attachments.AsNoTracking().AnyAsync(a => a.Id == id, cancellationToken))
        {
            throw NotFound();
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM attachments WHERE id = {id} FOR NO KEY UPDATE", cancellationToken);
        var attachment = await WithPeople(db.Attachments).SingleAsync(a => a.Id == id, cancellationToken);
        RequireAccess(attachment, write: true);
        if (attachment.Status == AttachmentStatus.Withdrawn)
        {
            throw Conflict("The attachment is already withdrawn.");
        }

        attachment.Status = AttachmentStatus.Withdrawn;
        attachment.WithdrawnAt = TimestampsInterceptor.ToDatabasePrecision(timeProvider.GetUtcNow());
        attachment.WithdrawnByUserId = CallerId;
        attachment.WithdrawnBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken);
        attachment.WithdrawReason = request.Reason!.Trim();
        audit.Record(
            AuditActions.AttachmentWithdrawn, nameof(Attachment), id,
            new { ownerType = attachment.OwnerType, from = AttachmentStatus.Active, to = AttachmentStatus.Withdrawn });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AttachmentResponse.From(attachment, zone);
    }

    // ---------- Helpers ----------

    private async Task<PagedResponse<AttachmentResponse>> ListAsync(
        string ownerEntity,
        Guid ownerId,
        Func<Task<bool>> ownerExists,
        System.Linq.Expressions.Expression<Func<Attachment, bool>> ofOwner,
        AttachmentListQuery query,
        CancellationToken cancellationToken)
    {
        if (!await ownerExists())
        {
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"The {ownerEntity.ToLowerInvariant()} was not found.");
        }

        var zone = await ZoneAsync(cancellationToken);
        var status = query.ParsedStatus;
        var page = await WithPeople(db.Attachments.AsNoTracking())
            .Where(ofOwner)
            .Where(a => a.Status == status)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .ToPageAsync(query, a => AttachmentResponse.From(a, zone), cancellationToken);

        audit.Record(AuditActions.AttachmentsListed, ownerEntity, ownerId, new { attachmentIds = page.Data.Select(a => a.Id).ToList() });
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    /// <summary>
    /// The owner of a new file, checked before the upload's body is read: another clinic's or an unknown owner is 404,
    /// a discarded consultation 409. The consultation is checked again under its lock when the row is inserted.
    /// </summary>
    public async Task<AttachmentOwner> ResolveOwnerAsync(
        AttachmentOwnerType ownerType,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        switch (ownerType)
        {
            case AttachmentOwnerType.Patient:
                return await db.Patients.AnyAsync(p => p.Id == ownerId, cancellationToken)
                    ? new AttachmentOwner(ownerType, ownerId, null, null)
                    : throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The patient was not found.");
            case AttachmentOwnerType.Client:
                return await db.Clients.AnyAsync(c => c.Id == ownerId, cancellationToken)
                    ? new AttachmentOwner(ownerType, null, null, ownerId)
                    : throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The client was not found.");
            default:
                var consultation = await db.Consultations.AsNoTracking()
                    .Where(c => c.Id == ownerId)
                    .Select(c => new { c.PatientId, c.Status })
                    .SingleOrDefaultAsync(cancellationToken)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The consultation was not found.");
                return consultation.Status == ConsultationStatus.Discarded
                    ? throw Conflict("The consultation was discarded; files cannot be attached to it.")
                    : new AttachmentOwner(ownerType, consultation.PatientId, ownerId, null);
        }
    }

    private static IFormFile CheckForm(AttachmentUpload upload)
    {
        var problems = new List<ApiErrorDetail>();
        if (upload.FileCount != 1 || upload.File is null)
        {
            problems.Add(new ApiErrorDetail("file", "Send exactly one file in the 'file' field."));
        }
        else if (upload.File.Length == 0)
        {
            problems.Add(new ApiErrorDetail("file", "The file is empty."));
        }

        if (upload.Description is { Length: > Attachment.DescriptionMaxLength })
        {
            problems.Add(new ApiErrorDetail("description", $"At most {Attachment.DescriptionMaxLength} characters."));
        }

        if (problems.Count > 0)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.", problems);
        }

        return upload.File!;
    }

    /// <summary>Client files follow the client permissions; patient and consultation files are clinical records.</summary>
    private void RequireAccess(Attachment attachment, bool write)
    {
        var permission = attachment.OwnerType == AttachmentOwnerType.Client
            ? write ? Permissions.ClientsManage : Permissions.ClientsRead
            : write ? Permissions.ClinicalWrite : Permissions.ClinicalRead;
        if (!Permissions.Grant(Permissions.RolesOf(httpContextAccessor.HttpContext!.User), permission))
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "You do not have permission to perform this action.");
        }
    }

    public static ApiException TooLarge() =>
        new(StatusCodes.Status413PayloadTooLarge, ErrorCodes.PayloadTooLarge,
            $"Files can be at most {Attachment.MaxSizeBytes / (1024 * 1024)} MB.");

    private async Task RemoveContentAsync(string key)
    {
        try
        {
            await files.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not remove the content of an attachment that was not saved.");
        }
    }

    private static IQueryable<Attachment> WithPeople(IQueryable<Attachment> attachments) =>
        attachments.Include(a => a.UploadedBy).Include(a => a.WithdrawnBy);

    private Guid CurrentClinicId =>
        tenant.ClinicId ?? throw new InvalidOperationException("Attachments require a current clinic.");

    private Guid CallerId =>
        AuthClaims.GetUserId(httpContextAccessor.HttpContext!.User)
        ?? throw new InvalidOperationException("Attachments require a signed-in user.");

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) =>
        ClinicTimeZone.Of(await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken));

    private static ApiException NotFound() => new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The attachment was not found.");

    private static ApiException Conflict(string message) => new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, message);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
