using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Scheduling;

/// <summary>Scheduling granularity (SRS Q-3, ADR-008): every time and instant is on a 5-minute mark.</summary>
public static class ScheduleRules
{
    public const int GranularityMinutes = 5;
    public const int MinutesPerDay = 24 * 60;
    public const int ReasonMaxLength = 500;

    /// <summary>The longest range the computed working hours endpoint accepts, in days.</summary>
    public const int MaxWorkingHoursRangeDays = 62;

    /// <summary>Most weekly-hours blocks (all versions) one veterinarian may have; keeps every read bounded.</summary>
    public const int MaxBlocksPerVeterinarian = 200;

    /// <summary>
    /// Dates and instants must fall in 2000–2100. Keeps calendar arithmetic away from DateTime's limits (year 0001 is
    /// stored as -infinity by Npgsql, year 9999 overflows) and catches typos such as 0202.
    /// </summary>
    public static readonly DateOnly MinSupportedDate = new(2000, 1, 1);

    public static readonly DateOnly MaxSupportedDate = new(2100, 12, 31);

    public const string SupportedRangeMessage = "Dates must be between 2000-01-01 and 2100-12-31.";

    public static bool IsInSupportedRange(DateOnly date) => date >= MinSupportedDate && date <= MaxSupportedDate;

    public static bool IsInSupportedRange(DateTimeOffset instant) =>
        IsInSupportedRange(DateOnly.FromDateTime(instant.UtcDateTime));

    public static bool IsOnGranularity(int minuteOfDay) => minuteOfDay % GranularityMinutes == 0;

    /// <summary>On the grid in UTC; every real time zone offset is a multiple of 5 minutes, so also locally.</summary>
    public static bool IsOnGranularity(DateTimeOffset instant) =>
        instant.UtcTicks % (TimeSpan.TicksPerMinute * GranularityMinutes) == 0;
}

/// <summary>
/// One block of a veterinarian's regular week, in the clinic's local time (docs/DATABASE.md section 9):
/// e.g. Mondays 09:00–12:30, valid from a date and optionally until a date (inclusive). Several blocks per day are
/// allowed; blocks of one veterinarian on one day may not overlap while both are in effect.
/// </summary>
/// <remarks>
/// Minutes of the day rather than <c>time</c>, so a block can end at midnight (1440, shown as "24:00"). A shift that
/// crosses midnight is two blocks.
/// </remarks>
public sealed class WorkingHoursBlock : Entity, IClinicOwned
{
    public Guid ClinicId { get; set; }

    public Guid VeterinarianId { get; set; }

    public DayOfWeek DayOfWeek { get; set; }

    /// <summary>Minutes after local midnight, 0–1435, on the 5-minute grid.</summary>
    public int StartMinute { get; set; }

    /// <summary>Minutes after local midnight, 5–1440, after <see cref="StartMinute"/>.</summary>
    public int EndMinute { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    /// <summary>Last day the block applies (inclusive); null for open-ended.</summary>
    public DateOnly? EffectiveTo { get; set; }

    public bool AppliesOn(DateOnly date) =>
        date.DayOfWeek == DayOfWeek && date >= EffectiveFrom && (EffectiveTo is null || date <= EffectiveTo);
}

/// <summary>
/// Time a veterinarian is not available (leave, sickness, ...) or, with no veterinarian, a clinic-wide closure that
/// blocks everyone (docs/DATABASE.md section 9). Stored as instants; the clinic time zone does not affect it.
/// </summary>
public sealed class ScheduleException : Entity, IClinicOwned
{
    public Guid ClinicId { get; set; }

    /// <summary>Null exactly when <see cref="Type"/> is <see cref="ScheduleExceptionType.Closure"/>.</summary>
    public Guid? VeterinarianId { get; set; }

    public ScheduleExceptionType Type { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    /// <summary>
    /// Free text; may contain personal or health information about staff, so it is shown only to schedule managers
    /// and the veterinarian concerned (ADR-008).
    /// </summary>
    public string? Reason { get; set; }
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum ScheduleExceptionType
{
    Leave,
    Sickness,
    Training,
    Other,

    /// <summary>Clinic-wide (no veterinarian), e.g. a public holiday.</summary>
    Closure,
}

internal sealed class WorkingHoursBlockConfiguration : IEntityTypeConfiguration<WorkingHoursBlock>
{
    public void Configure(EntityTypeBuilder<WorkingHoursBlock> builder)
    {
        builder.ToTable("working_hours", table =>
        {
            table.HasCheckConstraint(
                "ck_working_hours_day_of_week",
                $"day_of_week IN ({string.Join(", ", Enum.GetNames<DayOfWeek>().Select(name => $"'{name}'"))})");
            table.HasCheckConstraint(
                "ck_working_hours_minutes",
                $"start_minute >= 0 AND end_minute <= {ScheduleRules.MinutesPerDay} AND start_minute < end_minute " +
                $"AND start_minute % {ScheduleRules.GranularityMinutes} = 0 AND end_minute % {ScheduleRules.GranularityMinutes} = 0");
            table.HasCheckConstraint(
                "ck_working_hours_effective", "effective_to IS NULL OR effective_to >= effective_from");
        });
        builder.Property(block => block.DayOfWeek).HasConversion<string>().HasMaxLength(16);

        // The veterinarian is a user of the same clinic (composite key, ADR-005). Restrict: users are never deleted.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(block => new { block.ClinicId, block.VeterinarianId })
            .HasPrincipalKey(user => new { user.ClinicId, user.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(block => block.ClinicId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(block => new { block.ClinicId, block.VeterinarianId, block.DayOfWeek, block.StartMinute });
    }
}

internal sealed class ScheduleExceptionConfiguration : IEntityTypeConfiguration<ScheduleException>
{
    public void Configure(EntityTypeBuilder<ScheduleException> builder)
    {
        builder.ToTable("schedule_exceptions", table =>
        {
            table.HasCheckConstraint(
                "ck_schedule_exceptions_type",
                $"type IN ({string.Join(", ", Enum.GetNames<ScheduleExceptionType>().Select(name => $"'{name}'"))})");
            table.HasCheckConstraint(
                "ck_schedule_exceptions_closure", "(type = 'Closure') = (veterinarian_id IS NULL)");
            table.HasCheckConstraint("ck_schedule_exceptions_range", "ends_at > starts_at");
            table.HasCheckConstraint(
                "ck_schedule_exceptions_granularity",
                $"extract(epoch FROM starts_at) % {ScheduleRules.GranularityMinutes * 60} = 0 " +
                $"AND extract(epoch FROM ends_at) % {ScheduleRules.GranularityMinutes * 60} = 0");
        });
        builder.Property(exception => exception.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(exception => exception.Reason).HasMaxLength(ScheduleRules.ReasonMaxLength);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(exception => new { exception.ClinicId, exception.VeterinarianId })
            .HasPrincipalKey(user => new { user.ClinicId, user.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(exception => exception.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // Range queries: a veterinarian's exceptions (and closures, veterinarian null) overlapping a period.
        builder.HasIndex(exception => new { exception.ClinicId, exception.VeterinarianId, exception.StartsAt });
        builder.HasIndex(exception => new { exception.ClinicId, exception.StartsAt, exception.Id });
        builder.HasIndex(exception => new { exception.ClinicId, exception.VeterinarianId, exception.EndsAt });
    }
}
