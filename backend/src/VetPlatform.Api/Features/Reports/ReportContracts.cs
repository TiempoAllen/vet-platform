using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Patients;
using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Features.Reports;

/// <summary>Stored nowhere; sent as exact names like every enum (docs/API.md section 13).</summary>
public enum ReportGrouping
{
    Day,

    /// <summary>ISO weeks, Monday to Sunday.</summary>
    Week,
    Month,
}

/// <summary>
/// A report period (docs/API.md section 12, ADR-023): clinic-local dates <c>from</c> to <c>to</c> inclusive, at most
/// 366 days, cut into buckets by <c>groupBy</c>.
/// </summary>
public sealed class ReportQuery : IValidatableObject
{
    public const int MaxDays = 366;

    [Required]
    public DateOnly? From { get; init; }

    [Required]
    public DateOnly? To { get; init; }

    /// <summary>Day (default), Week or Month.</summary>
    public string? GroupBy { get; init; }

    /// <summary>Only this veterinarian's appointments.</summary>
    public Guid? VeterinarianId { get; init; }

    public ReportGrouping ParsedGroupBy => StrictEnum.TryParse<ReportGrouping>(GroupBy, out var grouping) ? grouping : ReportGrouping.Day;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (GroupBy is not null && !StrictEnum.TryParse<ReportGrouping>(GroupBy, out _))
        {
            yield return new ValidationResult($"GroupBy must be one of {StrictEnum.Names<ReportGrouping>()}.", [nameof(GroupBy)]);
        }

        if (From is not { } from || To is not { } to)
        {
            yield break;
        }

        if (!ScheduleRules.IsInSupportedRange(from) || !ScheduleRules.IsInSupportedRange(to))
        {
            yield return new ValidationResult(ScheduleRules.SupportedRangeMessage, [nameof(From)]);
        }
        else if (to < from || to.DayNumber - from.DayNumber >= MaxDays)
        {
            yield return new ValidationResult($"To must be on or after From, and the period at most {MaxDays} days.", [nameof(To)]);
        }
    }
}

/// <summary>
/// Appointment counts. The rates are fractions (0 to 1, 4 decimals) of the <em>resolved</em> appointments
/// (completed, cancelled and no-shows), so future bookings do not dilute them; null when none is resolved.
/// </summary>
public sealed record AppointmentCounts(
    int Total,
    int Scheduled,
    int CheckedIn,
    int Completed,
    int Cancelled,
    int CancelledByClient,
    int CancelledByClinic,
    int NoShow,
    decimal? NoShowRate,
    decimal? CancellationRate);

public sealed record AppointmentReportBucket(DateOnly Start, DateOnly End, AppointmentCounts Counts);

public sealed record AppointmentReportVeterinarian(AppointmentPersonSummary Veterinarian, AppointmentCounts Counts);

public sealed record AppointmentsReportResponse(
    DateOnly From,
    DateOnly To,
    ReportGrouping GroupBy,
    string TimeZone,
    AppointmentCounts Totals,
    IReadOnlyList<AppointmentReportVeterinarian> ByVeterinarian,
    IReadOnlyList<AppointmentReportBucket> Buckets);

/// <summary>
/// Patients seen: distinct patients with a completed appointment; <c>newPatients</c> had their first-ever completed
/// appointment in the period, the others are returning; <c>visits</c> counts the completed appointments.
/// </summary>
public sealed record PatientsSeenCounts(int Patients, int NewPatients, int ReturningPatients, int Visits);

public sealed record PatientsSeenBucket(DateOnly Start, DateOnly End, PatientsSeenCounts Counts);

public sealed record PatientsSeenSpecies(Species Species, PatientsSeenCounts Counts);

public sealed record PatientsSeenVeterinarian(AppointmentPersonSummary Veterinarian, PatientsSeenCounts Counts);

public sealed record PatientsSeenReportResponse(
    DateOnly From,
    DateOnly To,
    ReportGrouping GroupBy,
    string TimeZone,
    PatientsSeenCounts Totals,
    IReadOnlyList<PatientsSeenSpecies> BySpecies,
    IReadOnlyList<PatientsSeenVeterinarian> ByVeterinarian,
    IReadOnlyList<PatientsSeenBucket> Buckets);
