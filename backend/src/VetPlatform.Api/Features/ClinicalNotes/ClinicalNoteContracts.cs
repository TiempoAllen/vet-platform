using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Features.Appointments;

namespace VetPlatform.Api.Features.ClinicalNotes;

/// <summary>A clinical note as returned by the API (docs/API.md section 9). <c>createdAt</c> carries the clinic's offset.</summary>
public sealed record ClinicalNoteResponse(
    Guid Id,
    Guid PatientId,
    Guid? ConsultationId,
    string Text,
    AppointmentPersonSummary? Author,
    DateTimeOffset CreatedAt)
{
    public static ClinicalNoteResponse From(ClinicalNote n, TimeZoneInfo zone) =>
        new(
            n.Id, n.PatientId, n.ConsultationId, n.Text,
            n.Author is { } author ? new AppointmentPersonSummary(author.Id, author.Name) : null,
            TimeZoneInfo.ConvertTime(n.CreatedAt, zone));
}

/// <summary><c>consultationId</c> (optional) must be an in-progress or completed consultation of the same patient.</summary>
public sealed record AddClinicalNoteRequest(
    [Required, MaxLength(ClinicalNote.TextMaxLength)] string? Text,
    Guid? ConsultationId);
