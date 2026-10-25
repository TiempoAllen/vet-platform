using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Clients;
using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Features.Vaccinations;

/// <summary>A vaccination as returned by the API (docs/API.md section 9). <c>voidedAt</c> carries the clinic's offset.</summary>
public sealed record VaccinationResponse(
    Guid Id,
    Guid PatientId,
    Guid? ConsultationId,
    string VaccineName,
    string? Manufacturer,
    string? LotNumber,
    DateOnly? LotExpiresOn,
    DateOnly AdministeredOn,
    DateOnly? NextDueOn,
    string? AdministeredElsewhere,
    AppointmentPersonSummary? Veterinarian,
    string? Notes,
    VaccinationStatus Status,
    DateTimeOffset? VoidedAt,
    AppointmentPersonSummary? VoidedBy,
    string? VoidReason,
    AppointmentPersonSummary? RecordedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Veterinarian, voider and recorder must be loaded (a missing one shows null).</summary>
    public static VaccinationResponse From(Vaccination v, TimeZoneInfo zone) =>
        new(
            v.Id, v.PatientId, v.ConsultationId, v.VaccineName, v.Manufacturer, v.LotNumber, v.LotExpiresOn,
            v.AdministeredOn, v.NextDueOn, v.AdministeredElsewhere, Person(v.Veterinarian), v.Notes, v.Status,
            v.VoidedAt is { } at ? TimeZoneInfo.ConvertTime(at, zone) : null,
            Person(v.VoidedBy), v.VoidReason, Person(v.RecordedBy), v.CreatedAt, v.UpdatedAt);

    private static AppointmentPersonSummary? Person(Identity.User? user) =>
        user is null ? null : new AppointmentPersonSummary(user.Id, user.Name);
}

/// <summary>
/// Records a dose. <c>veterinarianId</c> is required for doses given here and must be omitted when
/// <c>administeredElsewhere</c> says where else it was given. Optional text may be omitted, null or blank.
/// </summary>
public sealed record RecordVaccinationRequest(
    [Required, MaxLength(Vaccination.VaccineNameMaxLength)] string? VaccineName,
    [MaxLength(Vaccination.ManufacturerMaxLength)] string? Manufacturer,
    [MaxLength(Vaccination.LotNumberMaxLength)] string? LotNumber,
    DateOnly? LotExpiresOn,
    [Required] DateOnly? AdministeredOn,
    DateOnly? NextDueOn,
    [MaxLength(Vaccination.AdministeredElsewhereMaxLength)] string? AdministeredElsewhere,
    Guid? VeterinarianId,
    Guid? ConsultationId,
    [MaxLength(Vaccination.NotesMaxLength)] string? Notes);

public sealed record VoidVaccinationRequest([Required, MaxLength(Vaccination.ReasonMaxLength)] string? Reason);

public sealed class VaccinationListQuery : PageQuery, IValidatableObject
{
    /// <summary>Active or Voided; without it both are listed.</summary>
    public string? Status { get; init; }

    public VaccinationStatus? ParsedStatus =>
        StrictEnum.TryParse<VaccinationStatus>(Status, out var status) ? status : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<VaccinationStatus>()}.", [nameof(Status)]);
        }
    }
}

/// <summary>The due list: <c>to</c> is required; without <c>from</c> every overdue dose is included.</summary>
public sealed class VaccinationDueQuery : PageQuery, IValidatableObject
{
    /// <summary>First due date, <c>YYYY-MM-DD</c> (optional).</summary>
    public string? From { get; init; }

    /// <summary>Last due date (inclusive), <c>YYYY-MM-DD</c>.</summary>
    [Required]
    public string? To { get; init; }

    public DateOnly? ParsedFrom => StrictInstant.TryParseDate(From, out var value) ? value : null;

    public DateOnly ParsedTo => StrictInstant.TryParseDate(To, out var value) ? value : default;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var fromValid = StrictInstant.TryParseDate(From, out var from);
        var toValid = StrictInstant.TryParseDate(To, out var to);
        if (From is not null && !fromValid)
        {
            yield return new ValidationResult("Expected a date as YYYY-MM-DD.", [nameof(From)]);
        }

        if (To is not null && !toValid)
        {
            yield return new ValidationResult("Expected a date as YYYY-MM-DD.", [nameof(To)]);
        }

        if (fromValid && !ScheduleRules.IsInSupportedRange(from))
        {
            yield return new ValidationResult(ScheduleRules.SupportedRangeMessage, [nameof(From)]);
        }

        if (toValid && !ScheduleRules.IsInSupportedRange(to))
        {
            yield return new ValidationResult(ScheduleRules.SupportedRangeMessage, [nameof(To)]);
        }

        if (fromValid && toValid && to < from)
        {
            yield return new ValidationResult("'to' must be on or after 'from'.", [nameof(To)]);
        }
    }
}

/// <summary>The owner as the due list shows it: enough to make the reminder call, nothing clinical.</summary>
public sealed record DueVaccinationClient(
    Guid Id,
    string FirstName,
    string LastName,
    string? Phone,
    string? Email,
    ContactMethod? PreferredContactMethod);

/// <summary>
/// A patient's latest active dose of a vaccine whose next dose falls due in the range. No lot, notes or other clinical
/// detail: the due list is readable by reception.
/// </summary>
public sealed record DueVaccinationResponse(
    Guid VaccinationId,
    string VaccineName,
    DateOnly LastAdministeredOn,
    DateOnly NextDueOn,
    AppointmentPatientSummary Patient,
    DueVaccinationClient? Client);
