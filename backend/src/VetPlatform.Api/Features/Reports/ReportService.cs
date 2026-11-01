using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;
using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Features.Reports;

/// <summary>
/// Operational reports (docs/API.md section 12, ADR-023). Read-only aggregates over the clinic's appointments, through
/// the clinic query filters: the few columns needed are fetched for the period and counted per clinic-local day, so
/// day, week and month buckets follow the clinic's time zone (daylight saving included). Counts only, never names of
/// patients or clients; not audited.
/// </summary>
public sealed class ReportService(AppDbContext db, ITenantContext tenant)
{
    public async Task<AppointmentsReportResponse> AppointmentsAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var (period, zone) = await PeriodAsync(query, cancellationToken);
        var appointments = InPeriod(period, query.VeterinarianId);
        var rows = await appointments
            .Select(a => new { a.StartAt, a.Status, a.CancelledBy, a.VeterinarianId })
            .ToListAsync(cancellationToken);
        var local = rows.Select(r => (Day: period.LocalDay(r.StartAt), r.Status, r.CancelledBy, r.VeterinarianId)).ToList();

        AppointmentCounts Count(IEnumerable<(DateOnly Day, AppointmentStatus Status, AppointmentCancelledBy? CancelledBy, Guid VeterinarianId)> items)
        {
            var list = items.ToList();
            int Of(AppointmentStatus status) => list.Count(i => i.Status == status);
            var completed = Of(AppointmentStatus.Completed);
            var cancelled = Of(AppointmentStatus.Cancelled);
            var noShow = Of(AppointmentStatus.NoShow);
            var resolved = completed + cancelled + noShow;
            return new AppointmentCounts(
                list.Count, Of(AppointmentStatus.Scheduled), Of(AppointmentStatus.CheckedIn), completed, cancelled,
                list.Count(i => i.Status == AppointmentStatus.Cancelled && i.CancelledBy == AppointmentCancelledBy.Client),
                list.Count(i => i.Status == AppointmentStatus.Cancelled && i.CancelledBy == AppointmentCancelledBy.Clinic),
                noShow, Rate(noShow, resolved), Rate(cancelled, resolved));
        }

        var vets = await VeterinariansAsync(local.Select(r => r.VeterinarianId), cancellationToken);
        var byDay = local.ToLookup(r => r.Day);
        return new AppointmentsReportResponse(
            period.From, period.To, period.Grouping, zone.Id, Count(local),
            local.GroupBy(r => r.VeterinarianId)
                .Select(g => new AppointmentReportVeterinarian(vets(g.Key), Count(g)))
                .OrderBy(v => v.Veterinarian.Name, StringComparer.Ordinal).ThenBy(v => v.Veterinarian.Id)
                .ToList(),
            period.Buckets().Select(b => new AppointmentReportBucket(b.Start, b.End, Count(Days(b).SelectMany(d => byDay[d])))).ToList());
    }

    public async Task<PatientsSeenReportResponse> PatientsSeenAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var (period, zone) = await PeriodAsync(query, cancellationToken);
        var rows = await InPeriod(period, query.VeterinarianId)
            .Where(a => a.Status == AppointmentStatus.Completed)
            .Select(a => new { a.PatientId, a.StartAt, a.VeterinarianId, a.Patient!.Species })
            .ToListAsync(cancellationToken);
        var visits = rows.Select(r => (r.PatientId, Day: period.LocalDay(r.StartAt), r.VeterinarianId, r.Species)).ToList();

        // A patient is new where its first-ever completed appointment (with any veterinarian) falls. The patients come
        // from a subquery, so no list of ids is sent to the database.
        var seen = InPeriod(period, query.VeterinarianId).Where(a => a.Status == AppointmentStatus.Completed).Select(a => a.PatientId);
        var firsts = await db.Appointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Completed && seen.Contains(a.PatientId))
            .GroupBy(a => a.PatientId)
            .Select(g => new { PatientId = g.Key, First = g.Min(a => a.StartAt) })
            .ToListAsync(cancellationToken);
        var firstDay = firsts.ToDictionary(f => f.PatientId, f => period.LocalDay(f.First));

        PatientsSeenCounts Count(IEnumerable<(Guid PatientId, DateOnly Day, Guid VeterinarianId, Species Species)> items, DateOnly start, DateOnly end)
        {
            var list = items.ToList();
            var patients = list.Select(v => v.PatientId).Distinct().ToList();
            var fresh = patients.Count(p => firstDay[p] >= start && firstDay[p] <= end);
            return new PatientsSeenCounts(patients.Count, fresh, patients.Count - fresh, list.Count);
        }

        var vets = await VeterinariansAsync(visits.Select(v => v.VeterinarianId), cancellationToken);
        var visitsByDay = visits.ToLookup(v => v.Day);
        return new PatientsSeenReportResponse(
            period.From, period.To, period.Grouping, zone.Id, Count(visits, period.From, period.To),
            visits.GroupBy(v => v.Species).OrderBy(g => g.Key)
                .Select(g => new PatientsSeenSpecies(g.Key, Count(g, period.From, period.To))).ToList(),
            visits.GroupBy(v => v.VeterinarianId)
                .Select(g => new PatientsSeenVeterinarian(vets(g.Key), Count(g, period.From, period.To)))
                .OrderBy(v => v.Veterinarian.Name, StringComparer.Ordinal).ThenBy(v => v.Veterinarian.Id)
                .ToList(),
            period.Buckets().Select(b => new PatientsSeenBucket(b.Start, b.End, Count(Days(b).SelectMany(d => visitsByDay[d]), b.Start, b.End))).ToList());
    }

    // ---------- Helpers ----------

    /// <summary>Appointments scheduled to start within the period (clinic-local days), optionally one veterinarian's.</summary>
    private IQueryable<Appointment> InPeriod(Period period, Guid? veterinarianId)
    {
        var appointments = db.Appointments.AsNoTracking().Where(a => a.StartAt >= period.StartsAt && a.StartAt < period.EndsBefore);
        return veterinarianId is { } vet ? appointments.Where(a => a.VeterinarianId == vet) : appointments;
    }

    /// <summary>Names of the clinic's users by id; one that cannot be shown appears with an empty name.</summary>
    private async Task<Func<Guid, AppointmentPersonSummary>> VeterinariansAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);
        return id => new AppointmentPersonSummary(id, names.GetValueOrDefault(id, ""));
    }

    private async Task<(Period Period, TimeZoneInfo Zone)> PeriodAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var clinicId = tenant.ClinicId ?? throw new InvalidOperationException("Reports require a current clinic.");
        var zone = ClinicTimeZone.Of(await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == clinicId, cancellationToken));
        return (new Period(query.From!.Value, query.To!.Value, query.ParsedGroupBy, zone), zone);
    }

    private static IEnumerable<DateOnly> Days((DateOnly Start, DateOnly End) bucket)
    {
        for (var day = bucket.Start; day <= bucket.End; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    private static decimal? Rate(int part, int whole) => whole == 0 ? null : decimal.Round((decimal)part / whole, 4, MidpointRounding.AwayFromZero);

    /// <summary>A clinic-local period and its buckets; the first and last buckets are clipped to the period.</summary>
    private sealed record Period(DateOnly From, DateOnly To, ReportGrouping Grouping, TimeZoneInfo Zone)
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
    }
}
