using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Scheduling;

/// <summary>Local times of day travel as <c>HH:mm</c> on the 5-minute grid; <c>24:00</c> is allowed as an end.</summary>
public static partial class TimeOfDay
{
    public static bool TryParse(string? value, bool allowEndOfDay, out int minuteOfDay)
    {
        minuteOfDay = 0;
        if (value is null || !Pattern().IsMatch(value))
        {
            return false;
        }

        var hours = int.Parse(value[..2], CultureInfo.InvariantCulture);
        var minutes = int.Parse(value[3..], CultureInfo.InvariantCulture);
        minuteOfDay = hours * 60 + minutes;
        return (hours < 24 || (allowEndOfDay && minutes == 0)) && minutes < 60 && ScheduleRules.IsOnGranularity(minuteOfDay);
    }

    public static string Format(int minuteOfDay) =>
        $"{minuteOfDay / 60:00}:{minuteOfDay % 60:00}";

    [GeneratedRegex(@"^\d{2}:\d{2}$")]
    private static partial Regex Pattern();
}

/// <summary>A veterinarian as listed for scheduling: enough to label a calendar column.</summary>
public sealed record VeterinarianResponse(Guid Id, string Name, UserStatus Status);

public sealed record WorkingHoursBlockResponse(
    Guid Id,
    Guid VeterinarianId,
    DayOfWeek DayOfWeek,
    string StartTime,
    string EndTime,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static WorkingHoursBlockResponse From(WorkingHoursBlock block) => new(
        block.Id, block.VeterinarianId, block.DayOfWeek, TimeOfDay.Format(block.StartMinute), TimeOfDay.Format(block.EndMinute),
        block.EffectiveFrom, block.EffectiveTo, block.CreatedAt, block.UpdatedAt);
}

public sealed record WorkingHoursBlockList(IReadOnlyList<WorkingHoursBlockResponse> Data);

/// <summary><c>effectiveFrom</c> defaults to today in the clinic's time zone.</summary>
public sealed record CreateWorkingHoursBlockRequest(
    [Required] DayOfWeek? DayOfWeek,
    [Required] string? StartTime,
    [Required] string? EndTime,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo);

/// <summary>Partial update; omitted or null fields are unchanged except <c>effectiveTo</c>, which an explicit null clears.</summary>
public sealed record UpdateWorkingHoursBlockRequest(
    DayOfWeek? DayOfWeek,
    string? StartTime,
    string? EndTime,
    DateOnly? EffectiveFrom,
    Optional<DateOnly?> EffectiveTo);

/// <summary>
/// What a reader sees of an exception type: the real type for schedule managers, the veterinarian concerned and
/// closures; <see cref="Unavailable"/> for everyone else, so staff absence reasons stay private (ADR-008).
/// </summary>
public enum ScheduleExceptionVisibleType
{
    Leave,
    Sickness,
    Training,
    Other,
    Closure,
    Unavailable,
}

public sealed record ScheduleExceptionResponse(
    Guid Id,
    Guid? VeterinarianId,
    ScheduleExceptionVisibleType Type,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ScheduleExceptionResponse From(ScheduleException exception, bool showDetails) => new(
        exception.Id,
        exception.VeterinarianId,
        showDetails || exception.Type == ScheduleExceptionType.Closure
            ? Enum.Parse<ScheduleExceptionVisibleType>(exception.Type.ToString())
            : ScheduleExceptionVisibleType.Unavailable,
        exception.StartsAt,
        exception.EndsAt,
        showDetails || exception.Type == ScheduleExceptionType.Closure ? exception.Reason : null,
        exception.CreatedAt,
        exception.UpdatedAt);
}

public sealed class ScheduleExceptionListQuery : PageQuery, IValidatableObject
{
    /// <summary>That veterinarian's exceptions plus clinic closures. Omitted: everything.</summary>
    public Guid? VeterinarianId { get; init; }

    /// <summary>Only exceptions ending after this instant (ISO 8601 with offset).</summary>
    public string? From { get; init; }

    /// <summary>Only exceptions starting before this instant (ISO 8601 with offset).</summary>
    public string? To { get; init; }

    public DateTimeOffset? ParsedFrom => StrictInstant.TryParse(From, out var value) ? value : null;

    public DateTimeOffset? ParsedTo => StrictInstant.TryParse(To, out var value) ? value : null;

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
    }

    private const string InstantMessage =
        "Expected an ISO 8601 date-time with an explicit offset, e.g. 2026-11-02T09:00:00Z (encode + as %2B).";
}

/// <summary><c>veterinarianId</c> null means a clinic-wide closure; then <c>type</c> must be <c>Closure</c>.</summary>
public sealed record CreateScheduleExceptionRequest(
    Guid? VeterinarianId,
    [Required] ScheduleExceptionType? Type,
    [Required] DateTimeOffset? StartsAt,
    [Required] DateTimeOffset? EndsAt,
    [MaxLength(ScheduleRules.ReasonMaxLength)] string? Reason);

/// <summary>Partial update. The veterinarian cannot be changed; <c>""</c> clears the reason.</summary>
public sealed record UpdateScheduleExceptionRequest(
    ScheduleExceptionType? Type,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    [MaxLength(ScheduleRules.ReasonMaxLength)] string? Reason);

public sealed class WorkingHoursQuery : IValidatableObject
{
    /// <summary>First local calendar day, <c>YYYY-MM-DD</c>.</summary>
    [Required]
    public string? From { get; init; }

    /// <summary>Last local calendar day (inclusive), <c>YYYY-MM-DD</c>.</summary>
    [Required]
    public string? To { get; init; }

    public DateOnly ParsedFrom => StrictInstant.TryParseDate(From, out var value) ? value : default;

    public DateOnly ParsedTo => StrictInstant.TryParseDate(To, out var value) ? value : default;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var fromValid = StrictInstant.TryParseDate(From, out var from);
        var toValid = StrictInstant.TryParseDate(To, out var to);
        if (!fromValid)
        {
            yield return new ValidationResult("Expected a date as YYYY-MM-DD.", [nameof(From)]);
        }

        if (!toValid)
        {
            yield return new ValidationResult("Expected a date as YYYY-MM-DD.", [nameof(To)]);
        }

        if (fromValid && toValid && (to < from || to.DayNumber - from.DayNumber >= ScheduleRules.MaxWorkingHoursRangeDays))
        {
            yield return new ValidationResult(
                $"'to' must be on or after 'from' and the range at most {ScheduleRules.MaxWorkingHoursRangeDays} days.", [nameof(To)]);
        }
    }
}

public sealed record WorkingHoursInterval(DateTimeOffset StartAt, DateTimeOffset EndAt);

/// <summary>Actual working periods; instants carry the clinic's local offset for that moment.</summary>
public sealed record WorkingHoursResponse(
    Guid VeterinarianId,
    string TimeZone,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<WorkingHoursInterval> Intervals);
