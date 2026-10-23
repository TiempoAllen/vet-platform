using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Features.Appointments;

namespace VetPlatform.Api.Features.Consultations;

public sealed record ConsultationVitals(
    decimal? WeightKg,
    decimal? TemperatureC,
    int? HeartRate,
    int? RespiratoryRate,
    int? BodyConditionScore,
    int? PainScore);

public sealed record ConsultationAddendumResponse(Guid Id, string Text, AppointmentPersonSummary? Author, DateTimeOffset CreatedAt);

/// <summary>A consultation as returned by the API (docs/API.md section 8). Clinical instants carry the clinic's offset.</summary>
public sealed record ConsultationResponse(
    Guid Id,
    AppointmentPatientSummary Patient,
    Guid? AppointmentId,
    AppointmentPersonSummary Veterinarian,
    ConsultationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    AppointmentPersonSummary? CompletedBy,
    DateTimeOffset? DiscardedAt,
    string? DiscardReason,
    string? Subjective,
    string? Objective,
    string? Assessment,
    string? Plan,
    ConsultationVitals Vitals,
    IReadOnlyList<ConsultationAddendumResponse> Addenda,
    AppointmentPersonSummary? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Patient, veterinarian, completer, creator and addenda (with authors) must be loaded.</summary>
    public static ConsultationResponse From(Consultation c, TimeZoneInfo zone)
    {
        var patient = c.Patient ?? throw new InvalidOperationException("The patient was not loaded.");
        var vet = c.Veterinarian ?? throw new InvalidOperationException("The veterinarian was not loaded.");
        return new(
            c.Id,
            new AppointmentPatientSummary(patient.Id, patient.Name, patient.Species),
            c.AppointmentId,
            new AppointmentPersonSummary(vet.Id, vet.Name),
            c.Status,
            TimeZoneInfo.ConvertTime(c.StartedAt, zone),
            Local(c.CompletedAt, zone),
            Person(c.CompletedBy),
            Local(c.DiscardedAt, zone),
            c.DiscardReason,
            c.Subjective,
            c.Objective,
            c.Assessment,
            c.Plan,
            new ConsultationVitals(
                Shortest(c.WeightKg), Shortest(c.TemperatureC), c.HeartRate, c.RespiratoryRate, c.BodyConditionScore, c.PainScore),
            c.Addenda.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .Select(a => new ConsultationAddendumResponse(a.Id, a.Text, Person(a.Author), TimeZoneInfo.ConvertTime(a.CreatedAt, zone)))
                .ToList(),
            Person(c.CreatedBy),
            c.CreatedAt,
            c.UpdatedAt);
    }

    private static AppointmentPersonSummary? Person(Identity.User? user) =>
        user is null ? null : new AppointmentPersonSummary(user.Id, user.Name);

    private static DateTimeOffset? Local(DateTimeOffset? instant, TimeZoneInfo zone) =>
        instant is { } value ? TimeZoneInfo.ConvertTime(value, zone) : null;

    /// <summary>numeric columns come back with their full scale (31.250); responses use the shortest form.</summary>
    private static decimal? Shortest(decimal? value) => value / 1.000000000000000000000000000000000m;
}

/// <summary>
/// Opens a consultation. <c>appointmentId</c> (optional) must be a CheckedIn appointment of the same patient;
/// <c>veterinarianId</c> must be an active veterinarian of the clinic.
/// </summary>
public sealed record CreateConsultationRequest(
    [Required] Guid? PatientId,
    [Required] Guid? VeterinarianId,
    Guid? AppointmentId,
    [MaxLength(Consultation.NotesMaxLength)] string? Subjective,
    [MaxLength(Consultation.NotesMaxLength)] string? Objective,
    [MaxLength(Consultation.NotesMaxLength)] string? Assessment,
    [MaxLength(Consultation.NotesMaxLength)] string? Plan,
    decimal? WeightKg,
    decimal? TemperatureC,
    int? HeartRate,
    int? RespiratoryRate,
    int? BodyConditionScore,
    int? PainScore);

/// <summary>
/// Edits an in-progress consultation. Omitted or null fields are unchanged; <c>""</c> clears a note. Each vital is an
/// <see cref="Optional{T}"/>: omitted leaves it, explicit null clears it.
/// </summary>
public sealed record UpdateConsultationRequest(
    Guid? VeterinarianId,
    [MaxLength(Consultation.NotesMaxLength)] string? Subjective,
    [MaxLength(Consultation.NotesMaxLength)] string? Objective,
    [MaxLength(Consultation.NotesMaxLength)] string? Assessment,
    [MaxLength(Consultation.NotesMaxLength)] string? Plan,
    Optional<decimal?> WeightKg,
    Optional<decimal?> TemperatureC,
    Optional<int?> HeartRate,
    Optional<int?> RespiratoryRate,
    Optional<int?> BodyConditionScore,
    Optional<int?> PainScore);

public sealed record DiscardConsultationRequest([Required, MaxLength(Consultation.ReasonMaxLength)] string? Reason);

public sealed record AddAddendumRequest([Required, MaxLength(ConsultationAddendum.TextMaxLength)] string? Text);

public sealed class ConsultationListQuery : PageQuery, IValidatableObject
{
    /// <summary>InProgress, Completed or Discarded. Without it, discarded consultations are left out.</summary>
    public string? Status { get; init; }

    public ConsultationStatus? ParsedStatus =>
        StrictEnum.TryParse<ConsultationStatus>(Status, out var status) ? status : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult(
                $"Status must be one of {StrictEnum.Names<ConsultationStatus>()}.", [nameof(Status)]);
        }
    }
}
