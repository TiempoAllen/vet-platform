using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Features.Appointments;

namespace VetPlatform.Api.Features.Attachments;

/// <summary>
/// An attachment's metadata as returned by the API (docs/API.md section 9). The content is fetched separately from
/// <c>/api/attachments/{id}/content</c>. <c>withdrawnAt</c> carries the clinic's offset.
/// </summary>
public sealed record AttachmentResponse(
    Guid Id,
    AttachmentOwnerType OwnerType,
    Guid? PatientId,
    Guid? ConsultationId,
    Guid? ClientId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string? Description,
    AttachmentStatus Status,
    DateTimeOffset? WithdrawnAt,
    AppointmentPersonSummary? WithdrawnBy,
    string? WithdrawReason,
    AppointmentPersonSummary? UploadedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Uploader and withdrawer must be loaded (a missing one shows null).</summary>
    public static AttachmentResponse From(Attachment a, TimeZoneInfo zone) =>
        new(
            a.Id, a.OwnerType, a.PatientId, a.ConsultationId, a.ClientId, a.FileName, a.ContentType, a.SizeBytes, a.Sha256,
            a.Description, a.Status,
            a.WithdrawnAt is { } at ? TimeZoneInfo.ConvertTime(at, zone) : null,
            Person(a.WithdrawnBy), a.WithdrawReason, Person(a.UploadedBy), a.CreatedAt, a.UpdatedAt);

    private static AppointmentPersonSummary? Person(Identity.User? user) =>
        user is null ? null : new AppointmentPersonSummary(user.Id, user.Name);
}

/// <summary>The parts of an upload's multipart form, read by the controller before the owner is checked.</summary>
public sealed record AttachmentUpload(IFormFile? File, int FileCount, string? Description);

public sealed record WithdrawAttachmentRequest([Required, MaxLength(Attachment.ReasonMaxLength)] string? Reason);

public sealed class AttachmentListQuery : PageQuery, IValidatableObject
{
    /// <summary>Active (the default) or Withdrawn.</summary>
    public string? Status { get; init; }

    public AttachmentStatus ParsedStatus =>
        StrictEnum.TryParse<AttachmentStatus>(Status, out var status) ? status : AttachmentStatus.Active;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && !StrictEnum.TryParse<AttachmentStatus>(Status, out _))
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<AttachmentStatus>()}.", [nameof(Status)]);
        }
    }
}

/// <summary>Recognises the accepted file types from their leading bytes; the name and the client's Content-Type are ignored.</summary>
public static class FileSignatures
{
    /// <summary>How many leading bytes <see cref="Detect"/> needs.</summary>
    public const int HeaderLength = 12;

    public static string? Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return head.StartsWith("%PDF-"u8) ? "application/pdf" : null;
    }

    /// <summary>
    /// The uploader's file name without any path, control characters or quotes, at most 255 characters; "file" when
    /// nothing is left. It is only ever used as the download name.
    /// </summary>
    public static string SafeFileName(string? name)
    {
        var last = name?.Split('/', '\\').LastOrDefault() ?? "";
        var cleaned = new string(last.Where(c => !char.IsControl(c) && c is not '"').ToArray()).Trim();
        if (cleaned.Length > Attachment.FileNameMaxLength)
        {
            cleaned = cleaned[..Attachment.FileNameMaxLength];
            if (char.IsHighSurrogate(cleaned[^1]))
            {
                cleaned = cleaned[..^1]; // never cut a character in half
            }

            cleaned = cleaned.Trim();
        }

        return cleaned.Length == 0 ? "file" : cleaned;
    }
}
