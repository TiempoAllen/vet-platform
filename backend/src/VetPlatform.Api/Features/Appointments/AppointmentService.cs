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
            // The start_at bound is redundant (no appointment exceeds MaxDuration) but lets the index bound the scan.
            var earliestStart = from.AddMinutes(-Appointment.MaxDurationMinutes);
            appointments = appointments.Where(a => a.EndAt > from && a.StartAt > earliestStart);
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

        // Lock order everywhere: appointment, then veterinarian, then patient (create locks no appointment row). The row
        // is locked only after the clinic-filtered lookup and read again afterwards, so two concurrent edits are
        // serialised and the second one checks the placement the first one left.
        if (!await db.Appointments.AsNoTracking().AnyAsync(a => a.Id == id, cancellationToken))
        {
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The appointment was not found.");
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM appointments WHERE id = {id} FOR NO KEY UPDATE", cancellationToken);
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

    // ---------- Status lifecycle (B-10, ADR-010) ----------

    /// <summary>Scheduled → CheckedIn, only on the appointment's local day.</summary>
    public Task<AppointmentResponse> CheckInAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, AuditActions.AppointmentCheckedIn, (appointment, context) =>
        {
            Require(appointment, "check in", AppointmentStatus.Scheduled);
            if (context.LocalDay(appointment.StartAt) != context.Today)
            {
                throw Conflict("An appointment can only be checked in on its own day.");
            }

            appointment.Status = AppointmentStatus.CheckedIn;
            appointment.CheckedInAt = context.Now;
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>CheckedIn → Scheduled, only on the day of the check-in.</summary>
    public Task<AppointmentResponse> UndoCheckInAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, AuditActions.AppointmentCheckInUndone, (appointment, context) =>
        {
            Require(appointment, "undo the check-in of", AppointmentStatus.CheckedIn);
            if (context.LocalDay(appointment.CheckedInAt!.Value) != context.Today)
            {
                throw Conflict("A check-in can only be undone on the day it was made.");
            }

            appointment.Status = AppointmentStatus.Scheduled;
            appointment.CheckedInAt = null;
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>CheckedIn → Completed (final).</summary>
    public Task<AppointmentResponse> CompleteAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, AuditActions.AppointmentCompleted, (appointment, context) =>
        {
            Require(appointment, "complete", AppointmentStatus.CheckedIn);
            appointment.Status = AppointmentStatus.Completed;
            appointment.CompletedAt = context.Now;
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>Scheduled or CheckedIn → Cancelled (final), recording who called it off.</summary>
    public Task<AppointmentResponse> CancelAsync(Guid id, CancelAppointmentRequest request, CancellationToken cancellationToken) =>
        TransitionAsync(id, AuditActions.AppointmentCancelled, (appointment, context) =>
        {
            Require(appointment, "cancel", AppointmentStatus.Scheduled, AppointmentStatus.CheckedIn);
            appointment.Status = AppointmentStatus.Cancelled;
            appointment.CancelledAt = context.Now;
            appointment.CancelledBy = request.CancelledBy!.Value;
            appointment.CancellationReason = NullIfBlank(request.Reason);
            return Task.CompletedTask;
        }, cancellationToken, metadata: a => new { cancelledBy = a.CancelledBy });

    /// <summary>Scheduled → NoShow, only once the appointment has started.</summary>
    public Task<AppointmentResponse> NoShowAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, AuditActions.AppointmentNoShow, (appointment, context) =>
        {
            Require(appointment, "mark as no-show", AppointmentStatus.Scheduled);
            if (context.Now < appointment.StartAt)
            {
                throw Conflict("An appointment can only be marked as a no-show once it has started.");
            }

            appointment.Status = AppointmentStatus.NoShow;
            appointment.NoShowAt = context.Now;
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>
    /// NoShow → Scheduled, only on the day it was marked, and only if the slot has not been given to someone else in
    /// the meantime (a no-show does not occupy the calendar, so it could have been).
    /// </summary>
    public Task<AppointmentResponse> UndoNoShowAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, AuditActions.AppointmentNoShowUndone, async (appointment, context) =>
        {
            Require(appointment, "undo the no-show of", AppointmentStatus.NoShow);
            if (context.LocalDay(appointment.NoShowAt!.Value) != context.Today)
            {
                throw Conflict("A no-show can only be undone on the day it was marked.");
            }

            // Same lock order as placements: appointment (held), veterinarian, patient.
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM users WHERE id = {appointment.VeterinarianId} FOR NO KEY UPDATE", cancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM patients WHERE id = {appointment.PatientId} FOR NO KEY UPDATE", cancellationToken);

            // Working hours are not re-judged (the booking was accepted then). Every clash is: a no-show frees the
            // slot, and an override accepted at booking time was for other appointments, never for whoever has been
            // booked into the freed slot since. Staff who still want both can re-book with an explicit override.
            var clashes = (await ConflictsAsync(appointment, context.Zone, cancellationToken))
                .Where(conflict => conflict != AppointmentConflict.OutsideWorkingHours)
                .ToList();
            if (clashes.Count > 0)
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                    "The slot has been booked again since the no-show; the no-show cannot be undone.",
                    clashes.Select(ConflictDetail).ToList());
            }

            appointment.Status = AppointmentStatus.Scheduled;
            appointment.NoShowAt = null;
        }, cancellationToken);

    /// <summary>The clock and calendar a transition is judged by.</summary>
    private sealed record TransitionContext(DateTimeOffset Now, TimeZoneInfo Zone)
    {
        public DateOnly Today => LocalDay(Now);

        public DateOnly LocalDay(DateTimeOffset instant) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);
    }

    /// <summary>
    /// Locks the appointment row (after the clinic-filtered lookup), re-reads it, applies one transition, records the
    /// actor and an audit entry, and saves. Concurrent transitions of one appointment are serialised, so the second
    /// one is judged against the status the first one left.
    /// </summary>
    private async Task<AppointmentResponse> TransitionAsync(
        Guid id,
        string auditAction,
        Func<Appointment, TransitionContext, Task> apply,
        CancellationToken cancellationToken,
        Func<Appointment, object>? metadata = null)
    {
        var zone = await ZoneAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Appointments.AsNoTracking().AnyAsync(a => a.Id == id, cancellationToken))
        {
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The appointment was not found.");
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM appointments WHERE id = {id} FOR NO KEY UPDATE", cancellationToken);
        var appointment = await FindAsync(id, tracking: true, cancellationToken);
        var before = appointment.Status;

        await apply(appointment, new TransitionContext(timeProvider.GetUtcNow(), zone));

        appointment.StatusChangedByUserId = CallerId;
        audit.Record(
            auditAction, nameof(Appointment), appointment.Id,
            new { from = before, to = appointment.Status, details = metadata?.Invoke(appointment) });
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AppointmentResponse.From(appointment, zone);
    }

    private static void Require(Appointment appointment, string action, params AppointmentStatus[] allowed)
    {
        if (!allowed.Contains(appointment.Status))
        {
            throw Conflict($"Cannot {action} an appointment that is {appointment.Status}.");
        }
    }

    private static ApiException Conflict(string message) =>
        new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, message);

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

        // The StartAt lower bound is redundant (no appointment exceeds MaxDuration) but keeps the index scan short,
        // which matters because this runs while the veterinarian and patient rows are locked.
        var earliestStart = start.AddMinutes(-Appointment.MaxDurationMinutes);
        var others = db.Appointments.AsNoTracking()
            .Where(other => other.Id != appointment.Id
                && Appointment.ActiveStatuses.Contains(other.Status)
                && other.StartAt < end && other.EndAt > start && other.StartAt > earliestStart);

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
        // Role and status are read again after the lock, so a concurrent demotion or disable cannot slip through.
        var veterinarian = exists
            ? await db.Users.Include(user => user.Roles).SingleAsync(user => user.Id == id, cancellationToken)
            : null;
        if (veterinarian is null
            || veterinarian.Status != UserStatus.Active
            || veterinarian.Roles.All(role => role.Role != Role.Veterinarian))
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
