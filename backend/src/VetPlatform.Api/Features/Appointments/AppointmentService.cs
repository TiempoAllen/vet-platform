using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;
using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Features.Appointments;

/// <summary>
/// Booking, editing and rescheduling appointments (docs/API.md section 7, ADR-009). Before every placement the
/// server checks the veterinarian's working hours (B-08), the veterinarian's and the patient's other active
/// appointments. Conflicts are a 409 unless the caller overrides them with a reason, which is recorded and audited.
/// </summary>
/// <remarks>
/// Placements take row locks on the veterinarian and then the patient, so two bookings cannot both pass the checks;
/// an exclusion constraint additionally forbids overlapping un-overridden bookings of one veterinarian.
/// </remarks>
public sealed class AppointmentService(
    AppDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    ScheduleService schedules,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor)
{
    public async Task<PagedResponse<AppointmentResponse>> ListAsync(AppointmentListQuery query, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var appointments = WithSummaries(db.Appointments.AsNoTracking());

        if (query.ParsedFrom is { } from)
        {
            appointments = appointments.Where(a => a.EndAt > from);
        }

        if (query.ParsedTo is { } to)
        {
            appointments = appointments.Where(a => a.StartAt < to);
        }

        if (query.VeterinarianId is { } veterinarianId)
        {
            appointments = appointments.Where(a => a.VeterinarianId == veterinarianId);
        }

        if (query.PatientId is { } patientId)
        {
            appointments = appointments.Where(a => a.PatientId == patientId);
        }

        if (query.ClientId is { } clientId)
        {
            appointments = appointments.Where(a => a.ClientId == clientId);
        }

        if (query.ParsedStatus is { } status)
        {
            appointments = appointments.Where(a => a.Status == status);
        }

        return await appointments.OrderBy(a => a.StartAt).ThenBy(a => a.Id)
            .ToPageAsync(query, a => AppointmentResponse.From(a, zone), cancellationToken);
    }

    public async Task<AppointmentResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        return AppointmentResponse.From(await FindAsync(id, tracking: false, cancellationToken), zone);
    }

    public async Task<AppointmentResponse> CreateAsync(CreateAppointmentRequest request, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var problems = new List<ApiErrorDetail>();
        var startAt = request.StartAt!.Value.ToUniversalTime();
        var endAt = request.EndAt!.Value.ToUniversalTime();
        CheckTimes(startAt, endAt, zone, problems);
        CheckOverrideFields(request.AllowConflicts, request.OverrideReason, problems);
        ThrowIfAny(problems);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var veterinarian = await LockVeterinarianAsync(request.VeterinarianId!.Value, problems, cancellationToken);
        var patient = await LockPatientAsync(request.PatientId!.Value, problems, cancellationToken);
        ThrowIfAny(problems);

        var appointment = new Appointment
        {
            ClinicId = CurrentClinicId,
            PatientId = patient!.Id,
            Patient = patient,
            ClientId = patient.ClientId,
            Client = patient.Client,
            VeterinarianId = veterinarian!.Id,
            Veterinarian = veterinarian,
            StartAt = startAt,
            EndAt = endAt,
            Type = request.Type!.Value,
            Reason = NullIfBlank(request.Reason),
            Notes = NullIfBlank(request.Notes),
            CreatedByUserId = CallerId,
        };
        await PlaceAsync(appointment, zone, request.AllowConflicts == true, request.OverrideReason, cancellationToken);

        db.Appointments.Add(appointment);
        audit.Record(
            AuditActions.AppointmentCreated, nameof(Appointment), appointment.Id,
            new
            {
                patientId = appointment.PatientId,
                veterinarianId = appointment.VeterinarianId,
                overriddenConflicts = appointment.OverriddenConflicts,
            });
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        appointment.CreatedBy = await db.Users.SingleOrDefaultAsync(u => u.Id == CallerId, cancellationToken);
        return AppointmentResponse.From(appointment, zone);
    }

    public async Task<AppointmentResponse> UpdateAsync(Guid id, UpdateAppointmentRequest request, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var problems = new List<ApiErrorDetail>();
        CheckOverrideFields(request.AllowConflicts, request.OverrideReason, problems);
        ThrowIfAny(problems);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var appointment = await FindAsync(id, tracking: true, cancellationToken);
        if (appointment.Status != AppointmentStatus.Scheduled)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Only scheduled appointments can be edited.");
        }

        var changed = new List<string>();
        var startAt = request.StartAt?.ToUniversalTime() ?? appointment.StartAt;
        var endAt = request.EndAt?.ToUniversalTime() ?? appointment.EndAt;
        var placementChanged =
            (request.PatientId is { } p && p != appointment.PatientId)
            || (request.VeterinarianId is { } v && v != appointment.VeterinarianId)
            || startAt != appointment.StartAt
            || endAt != appointment.EndAt;

        if (placementChanged)
        {
            CheckTimes(startAt, endAt, zone, problems);
            ThrowIfAny(problems);

            // Lock the veterinarian (current or new) first and then the patient, as on create.
            var veterinarian = await LockVeterinarianAsync(request.VeterinarianId ?? appointment.VeterinarianId, problems, cancellationToken);
            var patient = await LockPatientAsync(request.PatientId ?? appointment.PatientId, problems, cancellationToken);
            ThrowIfAny(problems);

            if (veterinarian!.Id != appointment.VeterinarianId)
            {
                appointment.VeterinarianId = veterinarian.Id;
                appointment.Veterinarian = veterinarian;
                changed.Add("veterinarianId");
            }

            if (patient!.Id != appointment.PatientId)
            {
                appointment.PatientId = patient.Id;
                appointment.Patient = patient;
                appointment.ClientId = patient.ClientId; // the client always follows the patient's current owner
                appointment.Client = patient.Client;
                changed.Add("patientId");
            }

            if (startAt != appointment.StartAt)
            {
                appointment.StartAt = startAt;
                changed.Add("startAt");
            }

            if (endAt != appointment.EndAt)
            {
                appointment.EndAt = endAt;
                changed.Add("endAt");
            }

            await PlaceAsync(appointment, zone, request.AllowConflicts == true, request.OverrideReason, cancellationToken);
        }

        if (request.Type is { } type && type != appointment.Type)
        {
            appointment.Type = type;
            changed.Add("type");
        }

        if (request.Reason is not null && NullIfBlank(request.Reason) != appointment.Reason)
        {
            appointment.Reason = NullIfBlank(request.Reason);
            changed.Add("reason");
        }

        if (request.Notes is not null && NullIfBlank(request.Notes) != appointment.Notes)
        {
            appointment.Notes = NullIfBlank(request.Notes);
            changed.Add("notes");
        }

        if (changed.Count > 0)
        {
            audit.Record(
                AuditActions.AppointmentUpdated, nameof(Appointment), appointment.Id,
                new
                {
                    fields = changed,
                    patientId = appointment.PatientId,
                    veterinarianId = appointment.VeterinarianId,
                    overriddenConflicts = appointment.OverriddenConflicts,
                });
            await SaveAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return AppointmentResponse.From(appointment, zone);
    }

    // ---------- Placement rules ----------

    /// <summary>
    /// Finds every conflict for the appointment's current placement. None: clears any earlier override. Some: a 409
    /// listing them, unless the caller overrides, in which case they are recorded with the reason.
    /// </summary>
    private async Task PlaceAsync(
        Appointment appointment,
        TimeZoneInfo zone,
        bool allowConflicts,
        string? overrideReason,
        CancellationToken cancellationToken)
    {
        var conflicts = await ConflictsAsync(appointment, zone, cancellationToken);
        if (conflicts.Count == 0)
        {
            appointment.OverriddenConflicts = [];
            appointment.OverrideReason = null;
            return;
        }

        if (!allowConflicts)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                "The appointment conflicts with the schedule. Choose another time, or override with a reason.",
                conflicts.Select(ConflictDetail).ToList());
        }

        appointment.OverriddenConflicts = conflicts;
        appointment.OverrideReason = overrideReason!.Trim();
    }

    private async Task<List<AppointmentConflict>> ConflictsAsync(
        Appointment appointment,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        var conflicts = new List<AppointmentConflict>();
        var (start, end) = (appointment.StartAt, appointment.EndAt);

        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end, zone).DateTime);
        var working = await schedules.WorkingIntervalsAsync(appointment.VeterinarianId, zone, firstDay, lastDay, cancellationToken);
        if (!working.Any(interval => interval.Start <= start && interval.End >= end))
        {
            conflicts.Add(AppointmentConflict.OutsideWorkingHours);
        }

        var others = db.Appointments.AsNoTracking()
            .Where(other => other.Id != appointment.Id
                && Appointment.ActiveStatuses.Contains(other.Status)
                && other.StartAt < end && other.EndAt > start);

        if (await others.AnyAsync(other => other.VeterinarianId == appointment.VeterinarianId, cancellationToken))
        {
            conflicts.Add(AppointmentConflict.VeterinarianDoubleBooked);
        }

        if (await others.AnyAsync(other => other.PatientId == appointment.PatientId, cancellationToken))
        {
            conflicts.Add(AppointmentConflict.PatientDoubleBooked);
        }

        return conflicts;
    }

    private static ApiErrorDetail ConflictDetail(AppointmentConflict conflict) => conflict switch
    {
        AppointmentConflict.OutsideWorkingHours => new ApiErrorDetail(
            "startAt", "OutsideWorkingHours: the veterinarian is not working for the whole of this time."),
        AppointmentConflict.VeterinarianDoubleBooked => new ApiErrorDetail(
            "veterinarianId", "VeterinarianDoubleBooked: the veterinarian has another appointment at this time."),
        _ => new ApiErrorDetail(
            "patientId", "PatientDoubleBooked: the patient has another appointment at this time."),
    };

    /// <summary>Grid, length, supported dates, and not before the start of today in the clinic's time zone.</summary>
    private void CheckTimes(DateTimeOffset startAt, DateTimeOffset endAt, TimeZoneInfo zone, List<ApiErrorDetail> problems)
    {
        foreach (var (field, value) in new[] { ("startAt", startAt), ("endAt", endAt) })
        {
            if (!ScheduleRules.IsInSupportedRange(value))
            {
                problems.Add(new ApiErrorDetail(field, ScheduleRules.SupportedRangeMessage));
            }
            else if (!ScheduleRules.IsOnGranularity(value))
            {
                problems.Add(new ApiErrorDetail(field, $"Must be on a {ScheduleRules.GranularityMinutes}-minute mark."));
            }
        }

        if (problems.Count > 0)
        {
            return;
        }

        var minutes = (endAt - startAt).TotalMinutes;
        if (minutes < Appointment.MinDurationMinutes || minutes > Appointment.MaxDurationMinutes)
        {
            problems.Add(new ApiErrorDetail(
                "endAt",
                $"An appointment lasts {Appointment.MinDurationMinutes} minutes to {Appointment.MaxDurationMinutes / 60} hours."));
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
        var startOfToday = WorkingHoursCalculator.ToInstant(today.ToDateTime(TimeOnly.MinValue), zone);
        if (startAt < startOfToday)
        {
            problems.Add(new ApiErrorDetail("startAt", "An appointment cannot start before today."));
        }
    }

    private static void CheckOverrideFields(bool? allowConflicts, string? overrideReason, List<ApiErrorDetail> problems)
    {
        if (allowConflicts == true && string.IsNullOrWhiteSpace(overrideReason))
        {
            problems.Add(new ApiErrorDetail("overrideReason", "A reason is required to override conflicts."));
        }
        else if (allowConflicts != true && overrideReason is not null)
        {
            problems.Add(new ApiErrorDetail("overrideReason", "Send overrideReason only together with allowConflicts: true."));
        }
    }

    /// <summary>
    /// An active veterinarian of this clinic, locked (FOR NO KEY UPDATE) so concurrent placements for them queue.
    /// Unknown, disabled, non-veterinarian and other clinics' users get the same 422.
    /// </summary>
    private async Task<User?> LockVeterinarianAsync(Guid id, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        var exists = await db.Users.AsNoTracking()
            .AnyAsync(user => user.Id == id && user.Roles.Any(role => role.Role == Role.Veterinarian), cancellationToken);
        if (exists)
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {id} FOR NO KEY UPDATE", cancellationToken);
        }

        // Tracked, not AsNoTracking: it becomes the appointment's navigation and must not be inserted as a new row.
        var veterinarian = exists ? await db.Users.SingleAsync(user => user.Id == id, cancellationToken) : null;
        if (veterinarian is null || veterinarian.Status != UserStatus.Active)
        {
            problems.Add(new ApiErrorDetail("veterinarianId", "The veterinarian was not found or is not active."));
            return null;
        }

        return veterinarian;
    }

    /// <summary>An active patient of this clinic with its owner, locked after the veterinarian. Same 422 for every miss.</summary>
    private async Task<Patient?> LockPatientAsync(Guid id, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        if (await db.Patients.AsNoTracking().AnyAsync(patient => patient.Id == id, cancellationToken))
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM patients WHERE id = {id} FOR NO KEY UPDATE", cancellationToken);
        }

        // Tracked for the same reason as the veterinarian.
        var patient = await db.Patients.Include(p => p.Client)
            .SingleOrDefaultAsync(p => p.Id == id && p.Status == PatientStatus.Active, cancellationToken);
        if (patient is null)
        {
            problems.Add(new ApiErrorDetail("patientId", "The patient was not found or is not active."));
        }

        return patient;
    }

    // ---------- Helpers ----------

    private Guid CurrentClinicId =>
        tenant.ClinicId ?? throw new InvalidOperationException("Appointments require a current clinic.");

    private Guid CallerId =>
        AuthClaims.GetUserId(httpContextAccessor.HttpContext!.User)
        ?? throw new InvalidOperationException("Appointments require a signed-in user.");

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) =>
        ClinicTimeZone.Of(await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken));

    private static IQueryable<Appointment> WithSummaries(IQueryable<Appointment> appointments) =>
        appointments
            .Include(a => a.Patient)
            .Include(a => a.Client)
            .Include(a => a.Veterinarian)
            .Include(a => a.CreatedBy);

    private async Task<Appointment> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var appointments = WithSummaries(tracking ? db.Appointments : db.Appointments.AsNoTracking());
        return await appointments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The appointment was not found.");
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
                  {
                      SqlState: PostgresErrorCodes.ExclusionViolation, ConstraintName: Appointment.NoOverlapConstraint,
                  })
        {
            // Defence in depth: the locks make this unreachable in normal operation.
            throw new ApiException(
                StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                "The appointment conflicts with the schedule. Choose another time, or override with a reason.",
                [ConflictDetail(AppointmentConflict.VeterinarianDoubleBooked)]);
        }
    }

    private static void ThrowIfAny(List<ApiErrorDetail> problems)
    {
        if (problems.Count > 0)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.",
                problems.ToList());
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
