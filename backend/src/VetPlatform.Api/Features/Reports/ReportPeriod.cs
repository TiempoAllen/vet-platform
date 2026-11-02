using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Features.Reports;

/// <summary>
/// A clinic-local report period and its buckets (ADR-023); the first and last buckets are clipped to the period. Rows
/// are placed on their clinic-local day in memory, so daylight-saving days stay right without database time-zone
/// arithmetic.
/// </summary>
internal sealed record ReportPeriod(DateOnly From, DateOnly To, ReportGrouping Grouping, TimeZoneInfo Zone)
{
    public DateTimeOffset StartsAt => WorkingHoursCalculator.ToInstant(From.ToDateTime(TimeOnly.MinValue), Zone);

    public DateTimeOffset EndsBefore => WorkingHoursCalculator.ToInstant(To.AddDays(1).ToDateTime(TimeOnly.MinValue), Zone);

    public DateOnly LocalDay(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);

    public IEnumerable<(DateOnly Start, DateOnly End)> Buckets()
    {
        var start = From;
        while (start <= To)
        {
            var next = Grouping switch
            {
                ReportGrouping.Week => start.AddDays(7 - ((int)start.DayOfWeek + 6) % 7),
                ReportGrouping.Month => new DateOnly(start.Year, start.Month, 1).AddMonths(1),
                _ => start.AddDays(1),
            };
            var end = next.AddDays(-1) < To ? next.AddDays(-1) : To;
            yield return (start, end);
            start = next;
        }
    }

    public static IEnumerable<DateOnly> Days((DateOnly Start, DateOnly End) bucket)
    {
        for (var day = bucket.Start; day <= bucket.End; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    /// <summary>The current clinic (its time zone and currency), read through the tenant.</summary>
    public static async Task<Clinic> ClinicAsync(AppDbContext db, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var clinicId = tenant.ClinicId ?? throw new InvalidOperationException("Reports require a current clinic.");
        return await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == clinicId, cancellationToken);
    }
}
