using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.Appointments;

public sealed record AppointmentPatientSummary(Guid Id, string Name, Species Species);

public sealed record AppointmentPersonSummary(Guid Id, string Name);

/// <summary>Present exactly when the appointment is Cancelled.</summary>
public sealed record AppointmentCancellation(DateTimeOffset At, AppointmentCancelledBy By, string? Reason);

/// <summary>Present when the appointment was placed despite conflicts.</summary>
public sealed record AppointmentOverride(string Reason, IReadOnlyList<AppointmentConflict> Conflicts);

/// <summary>An appointment as returned by the appointments API (docs/API.md section 7). Instants carry the clinic's offset.</summary>
public sealed record AppointmentResponse(
    Guid Id,
    AppointmentPatientSummary Patient,
    PatientClientSummary Client,
    AppointmentPersonSummary Veterinarian,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    AppointmentType Type,
    string? Reason,
    string? Notes,
    AppointmentStatus Status,
    DateTimeOffset? CheckedInAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? NoShowAt,
    AppointmentCancellation? Cancellation,
    AppointmentOverride? Override,
    AppointmentPersonSummary? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Patient, client, veterinarian and creator must be loaded.</summary>
    public static AppointmentResponse From(Appointment appointment, TimeZoneInfo zone)
    {
        var patient = appointment.Patient ?? throw new InvalidOperationException("The patient was not loaded.");
        var client = appointment.Client ?? throw new InvalidOperationException("The client was not loaded.");
        var veterinarian = appointment.Veterinarian ?? throw new InvalidOperationException("The veterinarian was not loaded.");
        return new(
            appointment.Id,
            new AppointmentPatientSummary(patient.Id, patient.Name, patient.Species),
            new PatientClientSummary(client.Id, client.FirstName, client.LastName),
            new AppointmentPersonSummary(veterinarian.Id, veterinarian.Name),
            TimeZoneInfo.ConvertTime(appointment.StartAt, zone),
            TimeZoneInfo.ConvertTime(appointment.EndAt, zone),
            appointment.Type,
            appointment.Reason,
            appointment.Notes,
            appointment.Status,
            Local(appointment.CheckedInAt, zone),
            Local(appointment.CompletedAt, zone),
            Local(appointment.NoShowAt, zone),
            appointment.CancelledAt is { } cancelledAt
                ? new AppointmentCancellation(
                    TimeZoneInfo.ConvertTime(cancelledAt, zone), appointment.CancelledBy!.Value, appointment.CancellationReason)
                : null,
            appointment.OverrideReason is { } reason ? new AppointmentOverride(reason, appointment.OverriddenConflicts) : null,
            appointment.CreatedBy is { } creator ? new AppointmentPersonSummary(creator.Id, creator.Name) : null,
            appointment.CreatedAt,
            appointment.UpdatedAt);
    }

    private static DateTimeOffset? Local(DateTimeOffset? instant, TimeZoneInfo zone) =>
        instant is { } value ? TimeZoneInfo.ConvertTime(value, zone) : null;
}

/// <summary><c>cancelledBy</c> says who called it off (Client or Clinic); the reason is optional.</summary>
public sealed record CancelAppointmentRequest(
    [Required] AppointmentCancelledBy? CancelledBy,
    [MaxLength(Appointment.ReasonMaxLength)] string? Reason);

public sealed class AppointmentListQuery : PageQuery, IValidatableObject
{
    /// <summary>Only appointments ending after this instant (ISO 8601 with offset).</summary>
    public string? From { get; init; }

    /// <summary>Only appointments starting before this instant (ISO 8601 with offset).</summary>
    public string? To { get; init; }

    public Guid? VeterinarianId { get; init; }

    public Guid? PatientId { get; init; }

    public Guid? ClientId { get; init; }

    /// <summary>An appointment status name. A string so only exact names are accepted.</summary>
    public string? Status { get; init; }

    public DateTimeOffset? ParsedFrom => StrictInstant.TryParse(From, out var value) ? value.ToUniversalTime() : null;

    public DateTimeOffset? ParsedTo => StrictInstant.TryParse(To, out var value) ? value.ToUniversalTime() : null;

    public AppointmentStatus? ParsedStatus =>
        StrictEnum.TryParse<AppointmentStatus>(Status, out var status) ? status : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From is not null && ParsedFrom is null)
        {
            yield return new ValidationResult(InstantMessage, [nameof(From)]);
        }

        if (To is not null && ParsedTo is null)
        {
            yield return new ValidationResult(InstantMessage, [nameof(To)]);
        }

        if (ParsedFrom is { } from && ParsedTo is { } to && to <= from)
        {
            yield return new ValidationResult("'to' must be after 'from'.", [nameof(To)]);
        }

        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult(
                $"Status must be one of {StrictEnum.Names<AppointmentStatus>()}.", [nameof(Status)]);
        }
    }

    private const string InstantMessage =
        "Expected an ISO 8601 date-time with an explicit offset, e.g. 2026-11-02T09:00:00Z (encode + as %2B).";
}

/// <summary>
/// The client is not sent: it is always the patient's current owner. <c>allowConflicts</c> with an
/// <c>overrideReason</c> accepts the conflicts the server finds (Q-3, ADR-009).
/// </summary>
public sealed record CreateAppointmentRequest(
    [Required] Guid? PatientId,
    [Required] Guid? VeterinarianId,
    [Required] DateTimeOffset? StartAt,
    [Required] DateTimeOffset? EndAt,
    [Required] AppointmentType? Type,
    [MaxLength(Appointment.ReasonMaxLength)] string? Reason,
    [MaxLength(Appointment.NotesMaxLength)] string? Notes,
    bool? AllowConflicts,
    [MaxLength(Appointment.ReasonMaxLength)] string? OverrideReason);

/// <summary>
/// Partial update (edit and reschedule). Omitted or null fields are unchanged; <c>""</c> clears reason and notes.
/// Changing the patient, veterinarian or times re-checks conflicts; the override fields then apply as on create.
/// </summary>
public sealed record UpdateAppointmentRequest(
    Guid? PatientId,
    Guid? VeterinarianId,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt,
    AppointmentType? Type,
    [MaxLength(Appointment.ReasonMaxLength)] string? Reason,
    [MaxLength(Appointment.NotesMaxLength)] string? Notes,
    bool? AllowConflicts,
    [MaxLength(Appointment.ReasonMaxLength)] string? OverrideReason);
