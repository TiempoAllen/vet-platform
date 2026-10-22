using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Scheduling;

/// <summary>
/// Veterinarians' weekly working hours, schedule exceptions, clinic closures and the computed working periods
/// (docs/API.md section 7.1, ADR-008). Everything is clinic-filtered; another clinic's veterinarian or exception is 404.
/// </summary>
/// <remarks>
/// Writes need <c>schedules.manage</c> (anyone's schedule, closures) or <c>schedules.manage_own</c> for the caller's
/// own schedule. Audit metadata never contains exception reasons (they may be health information).
/// </remarks>
public sealed class ScheduleService(AppDbContext db, ITenantContext tenant, AuditTrail audit, TimeProvider timeProvider)
{
    // ---------- Veterinarians ----------

    public Task<PagedResponse<VeterinarianResponse>> ListVeterinariansAsync(PageQuery page, CancellationToken cancellationToken) =>
        Veterinarians()
            .Where(user => user.Status == UserStatus.Active)
            .OrderBy(user => user.Name).ThenBy(user => user.Id)
            .ToPageAsync(page, user => new VeterinarianResponse(user.Id, user.Name, user.Status), cancellationToken);

    // ---------- Weekly hours ----------

    public async Task<WorkingHoursBlockList> ListBlocksAsync(Guid veterinarianId, CancellationToken cancellationToken)
    {
        await FindVeterinarianAsync(veterinarianId, requireActive: false, cancellationToken);
        var blocks = await db.WorkingHours.AsNoTracking()
            .Where(block => block.VeterinarianId == veterinarianId)
            .ToListAsync(cancellationToken);
        return new WorkingHoursBlockList(blocks
            .OrderBy(block => DayIndex(block.DayOfWeek)).ThenBy(block => block.StartMinute)
            .ThenBy(block => block.EffectiveFrom).ThenBy(block => block.Id)
            .Select(WorkingHoursBlockResponse.From).ToList());
    }

    public async Task<WorkingHoursBlockResponse> CreateBlockAsync(
        ClaimsPrincipal caller,
        Guid veterinarianId,
        CreateWorkingHoursBlockRequest request,
        CancellationToken cancellationToken)
    {
        EnsureCanManage(caller, veterinarianId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await FindVeterinarianAsync(veterinarianId, requireActive: true, cancellationToken, lockRow: true);

        var problems = new List<ApiErrorDetail>();
        var start = ParseTime("startTime", request.StartTime, allowEndOfDay: false, problems);
        var end = ParseTime("endTime", request.EndTime, allowEndOfDay: true, problems);
        var block = new WorkingHoursBlock
        {
            ClinicId = CurrentClinicId,
            VeterinarianId = veterinarianId,
            DayOfWeek = request.DayOfWeek!.Value,
            StartMinute = start,
            EndMinute = end,
            EffectiveFrom = request.EffectiveFrom ?? await TodayAsync(cancellationToken),
            EffectiveTo = request.EffectiveTo,
        };
        CheckBlock(block, problems);
        ThrowIfAny(problems);
        await EnsureNoOverlapAsync(block, cancellationToken);
        if (await db.WorkingHours.CountAsync(b => b.VeterinarianId == veterinarianId, cancellationToken)
            >= ScheduleRules.MaxBlocksPerVeterinarian)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                $"A veterinarian can have at most {ScheduleRules.MaxBlocksPerVeterinarian} weekly-hours blocks; delete old ones first.");
        }

        db.WorkingHours.Add(block);
        audit.Record(AuditActions.WorkingHoursCreated, nameof(WorkingHoursBlock), block.Id, new { veterinarianId });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return WorkingHoursBlockResponse.From(block);
    }

    public async Task<WorkingHoursBlockResponse> UpdateBlockAsync(
        ClaimsPrincipal caller,
        Guid veterinarianId,
        Guid blockId,
        UpdateWorkingHoursBlockRequest request,
        CancellationToken cancellationToken)
    {
        EnsureCanManage(caller, veterinarianId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await FindVeterinarianAsync(veterinarianId, requireActive: false, cancellationToken, lockRow: true);
        var block = await FindBlockAsync(veterinarianId, blockId, cancellationToken);

        var problems = new List<ApiErrorDetail>();
        var changed = new List<string>();
        if (request.DayOfWeek is { } day && day != block.DayOfWeek)
        {
            block.DayOfWeek = day;
            changed.Add("dayOfWeek");
        }

        if (request.StartTime is not null && ParseTime("startTime", request.StartTime, false, problems) is var start && start != block.StartMinute)
        {
            block.StartMinute = start;
            changed.Add("startTime");
        }

        if (request.EndTime is not null && ParseTime("endTime", request.EndTime, true, problems) is var end && end != block.EndMinute)
        {
            block.EndMinute = end;
            changed.Add("endTime");
        }

        if (request.EffectiveFrom is { } effectiveFrom && effectiveFrom != block.EffectiveFrom)
        {
            block.EffectiveFrom = effectiveFrom;
            changed.Add("effectiveFrom");
        }

        if (request.EffectiveTo.IsSet && request.EffectiveTo.Value != block.EffectiveTo)
        {
            block.EffectiveTo = request.EffectiveTo.Value;
            changed.Add("effectiveTo");
        }

        CheckBlock(block, problems);
        ThrowIfAny(problems);
        if (changed.Count > 0)
        {
            await EnsureNoOverlapAsync(block, cancellationToken);
            audit.Record(AuditActions.WorkingHoursUpdated, nameof(WorkingHoursBlock), block.Id, new { veterinarianId, fields = changed });
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return WorkingHoursBlockResponse.From(block);
    }

    public async Task DeleteBlockAsync(ClaimsPrincipal caller, Guid veterinarianId, Guid blockId, CancellationToken cancellationToken)
    {
        EnsureCanManage(caller, veterinarianId);
        await FindVeterinarianAsync(veterinarianId, requireActive: false, cancellationToken);
        var block = await FindBlockAsync(veterinarianId, blockId, cancellationToken);
        db.WorkingHours.Remove(block);
        audit.Record(AuditActions.WorkingHoursDeleted, nameof(WorkingHoursBlock), block.Id, new { veterinarianId });
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---------- Exceptions and closures ----------

    public Task<PagedResponse<ScheduleExceptionResponse>> ListExceptionsAsync(
        ClaimsPrincipal caller,
        ScheduleExceptionListQuery query,
        CancellationToken cancellationToken)
    {
        var exceptions = db.ScheduleExceptions.AsNoTracking();
        if (query.VeterinarianId is { } veterinarianId)
        {
            exceptions = exceptions.Where(e => e.VeterinarianId == veterinarianId || e.VeterinarianId == null);
        }

        // Npgsql writes timestamptz parameters only with offset 0, so normalise "+01:00" input to UTC.
        if (query.ParsedFrom?.ToUniversalTime() is { } from)
        {
            exceptions = exceptions.Where(e => e.EndsAt > from);
        }

        if (query.ParsedTo?.ToUniversalTime() is { } to)
        {
            exceptions = exceptions.Where(e => e.StartsAt < to);
        }

        var managesAll = Has(caller, Permissions.SchedulesManage);
        var callerId = AuthClaims.GetUserId(caller);
        return exceptions.OrderBy(e => e.StartsAt).ThenBy(e => e.Id)
            .ToPageAsync(
                query,
                e => ScheduleExceptionResponse.From(e, showDetails: managesAll || (e.VeterinarianId is { } vet && vet == callerId)),
                cancellationToken);
    }

    public async Task<ScheduleExceptionResponse> GetExceptionAsync(ClaimsPrincipal caller, Guid id, CancellationToken cancellationToken)
    {
        var exception = await FindExceptionAsync(id, tracking: false, cancellationToken);
        return ScheduleExceptionResponse.From(exception, ShowsDetails(caller, exception));
    }

    public async Task<ScheduleExceptionResponse> CreateExceptionAsync(
        ClaimsPrincipal caller,
        CreateScheduleExceptionRequest request,
        CancellationToken cancellationToken)
    {
        var type = request.Type!.Value;
        var problems = new List<ApiErrorDetail>();
        if ((type == ScheduleExceptionType.Closure) != (request.VeterinarianId is null))
        {
            problems.Add(new ApiErrorDetail(
                request.VeterinarianId is null ? "veterinarianId" : "type",
                "A closure has no veterinarian; every other type needs one."));
            ThrowIfAny(problems);
        }

        EnsureCanManage(caller, request.VeterinarianId);
        if (request.VeterinarianId is { } veterinarianId)
        {
            await FindVeterinarianAsync(veterinarianId, requireActive: true, cancellationToken);
        }

        var exception = new ScheduleException
        {
            ClinicId = CurrentClinicId,
            VeterinarianId = request.VeterinarianId,
            Type = type,
            StartsAt = request.StartsAt!.Value.ToUniversalTime(),
            EndsAt = request.EndsAt!.Value.ToUniversalTime(),
            Reason = NullIfBlank(request.Reason),
        };
        CheckRange(exception, problems);
        ThrowIfAny(problems);

        db.ScheduleExceptions.Add(exception);
        audit.Record(
            AuditActions.ScheduleExceptionCreated, nameof(ScheduleException), exception.Id,
            AuditMetadata(exception));
        await db.SaveChangesAsync(cancellationToken);
        return ScheduleExceptionResponse.From(exception, showDetails: true);
    }

    public async Task<ScheduleExceptionResponse> UpdateExceptionAsync(
        ClaimsPrincipal caller,
        Guid id,
        UpdateScheduleExceptionRequest request,
        CancellationToken cancellationToken)
    {
        var exception = await FindExceptionAsync(id, tracking: true, cancellationToken);
        EnsureCanManage(caller, exception.VeterinarianId);

        var problems = new List<ApiErrorDetail>();
        var changed = new List<string>();
        if (request.Type is { } type && type != exception.Type)
        {
            if ((type == ScheduleExceptionType.Closure) != (exception.Type == ScheduleExceptionType.Closure))
            {
                problems.Add(new ApiErrorDetail("type", "A closure cannot become a veterinarian's exception, or the reverse."));
            }

            exception.Type = type;
            changed.Add("type");
        }

        if (request.StartsAt is { } startsAt && startsAt != exception.StartsAt)
        {
            exception.StartsAt = startsAt.ToUniversalTime();
            changed.Add("startsAt");
        }

        if (request.EndsAt is { } endsAt && endsAt != exception.EndsAt)
        {
            exception.EndsAt = endsAt.ToUniversalTime();
            changed.Add("endsAt");
        }

        if (request.Reason is not null && NullIfBlank(request.Reason) != exception.Reason)
        {
            exception.Reason = NullIfBlank(request.Reason);
            changed.Add("reason");
        }

        CheckRange(exception, problems);
        ThrowIfAny(problems);
        if (changed.Count > 0)
        {
            audit.Record(
                AuditActions.ScheduleExceptionUpdated, nameof(ScheduleException), exception.Id,
                AuditMetadata(exception, changed));
            await db.SaveChangesAsync(cancellationToken);
        }

        return ScheduleExceptionResponse.From(exception, showDetails: true);
    }

    public async Task DeleteExceptionAsync(ClaimsPrincipal caller, Guid id, CancellationToken cancellationToken)
    {
        var exception = await FindExceptionAsync(id, tracking: true, cancellationToken);
        EnsureCanManage(caller, exception.VeterinarianId);
        db.ScheduleExceptions.Remove(exception);
        audit.Record(
            AuditActions.ScheduleExceptionDeleted, nameof(ScheduleException), exception.Id,
            AuditMetadata(exception));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Names the veterinarian and changed fields. The type is recorded only for closures: a personal absence type
    /// ("Sickness") is as private as its reason (ADR-008), and an audit reader may lack <c>schedules.manage</c>.
    /// </summary>
    private static object AuditMetadata(ScheduleException exception, List<string>? fields = null) =>
        exception.Type == ScheduleExceptionType.Closure
            ? new { veterinarianId = exception.VeterinarianId, type = exception.Type, fields }
            : new { veterinarianId = exception.VeterinarianId, type = (ScheduleExceptionType?)null, fields };

    // ---------- Computed working hours ----------

    public async Task<WorkingHoursResponse> WorkingHoursAsync(
        Guid veterinarianId,
        WorkingHoursQuery query,
        CancellationToken cancellationToken)
    {
        await FindVeterinarianAsync(veterinarianId, requireActive: false, cancellationToken);
        var clinic = await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken);
        var zone = ClinicTimeZone.Of(clinic);
        var (from, to) = (query.ParsedFrom, query.ParsedTo);
        var intervals = await WorkingIntervalsAsync(veterinarianId, zone, from, to, cancellationToken);
        return new WorkingHoursResponse(
            veterinarianId, clinic.TimeZone, from, to,
            intervals.Select(i => new WorkingHoursInterval(TimeZoneInfo.ConvertTime(i.Start, zone), TimeZoneInfo.ConvertTime(i.End, zone)))
                .ToList());
    }

    /// <summary>
    /// The veterinarian's working periods (UTC) for the local days <paramref name="from"/>..<paramref name="to"/>:
    /// weekly hours minus their exceptions and all clinic closures. Shared with appointment booking (B-09).
    /// </summary>
    public async Task<List<Interval>> WorkingIntervalsAsync(
        Guid veterinarianId,
        TimeZoneInfo zone,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        // Generous window in instants: local days can start up to 14 h before or after UTC midnight.
        var windowStart = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var windowEnd = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(2);

        var blocks = await db.WorkingHours.AsNoTracking()
            .Where(block => block.VeterinarianId == veterinarianId
                && block.EffectiveFrom <= to
                && (block.EffectiveTo == null || block.EffectiveTo >= from))
            .ToListAsync(cancellationToken);
        var unavailable = await db.ScheduleExceptions.AsNoTracking()
            .Where(e => (e.VeterinarianId == veterinarianId || e.VeterinarianId == null)
                && e.StartsAt < windowEnd && e.EndsAt > windowStart)
            .Select(e => new Interval(e.StartsAt, e.EndsAt))
            .ToListAsync(cancellationToken);

        return WorkingHoursCalculator.Compute(blocks, unavailable, zone, from, to);
    }

    // ---------- Helpers ----------

    private Guid CurrentClinicId =>
        tenant.ClinicId ?? throw new InvalidOperationException("Scheduling requires a current clinic.");

    /// <summary>Users of the clinic (query filter) holding the Veterinarian role.</summary>
    private IQueryable<User> Veterinarians() =>
        db.Users.AsNoTracking().Where(user => user.Roles.Any(role => role.Role == Role.Veterinarian));

    /// <summary>404 unless the id is a veterinarian of this clinic; writes also need the account to be active (422).</summary>
    private async Task FindVeterinarianAsync(Guid id, bool requireActive, CancellationToken cancellationToken, bool lockRow = false)
    {
        if (!await Veterinarians().AnyAsync(user => user.Id == id, cancellationToken))
        {
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The veterinarian was not found.");
        }

        if (lockRow)
        {
            // Serialises changes to one veterinarian's weekly hours, so two overlapping blocks cannot both pass the
            // check. Taken only after the clinic-filtered lookup, so no other clinic's row is ever locked. NO KEY UPDATE
            // does not block foreign-key checks of other tables that reference the user.
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {id} FOR NO KEY UPDATE", cancellationToken);
        }

        // Read after the lock, so a concurrent disable cannot slip between the check and the write.
        if (requireActive
            && !await Veterinarians().AnyAsync(user => user.Id == id && user.Status == UserStatus.Active, cancellationToken))
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.",
                [new ApiErrorDetail("veterinarianId", "The veterinarian's account is disabled.")]);
        }
    }

    private async Task<WorkingHoursBlock> FindBlockAsync(Guid veterinarianId, Guid blockId, CancellationToken cancellationToken) =>
        await db.WorkingHours.SingleOrDefaultAsync(b => b.Id == blockId && b.VeterinarianId == veterinarianId, cancellationToken)
        ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The working hours block was not found.");

    private async Task<ScheduleException> FindExceptionAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var exceptions = tracking ? db.ScheduleExceptions : db.ScheduleExceptions.AsNoTracking();
        return await exceptions.SingleOrDefaultAsync(e => e.Id == id, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The schedule exception was not found.");
    }

    /// <summary>
    /// <c>schedules.manage</c> may change anything; <c>schedules.manage_own</c> only the caller's own schedule. Clinic
    /// closures (<paramref name="veterinarianId"/> null) need <c>schedules.manage</c>.
    /// </summary>
    private static void EnsureCanManage(ClaimsPrincipal caller, Guid? veterinarianId)
    {
        var allowed = Has(caller, Permissions.SchedulesManage)
            || (veterinarianId is { } vet && vet == AuthClaims.GetUserId(caller) && Has(caller, Permissions.SchedulesManageOwn));
        if (!allowed)
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "You do not have permission to perform this action.");
        }
    }

    private static bool ShowsDetails(ClaimsPrincipal caller, ScheduleException exception) =>
        Has(caller, Permissions.SchedulesManage)
        || (exception.VeterinarianId is { } vet && vet == AuthClaims.GetUserId(caller));

    private static bool Has(ClaimsPrincipal caller, string permission) =>
        Permissions.Grant(Permissions.RolesOf(caller), permission);

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        var clinic = await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), ClinicTimeZone.Of(clinic)).DateTime);
    }

    private static int ParseTime(string field, string? value, bool allowEndOfDay, List<ApiErrorDetail> problems)
    {
        if (TimeOfDay.TryParse(value, allowEndOfDay, out var minute))
        {
            return minute;
        }

        problems.Add(new ApiErrorDetail(
            field,
            $"Expected HH:mm on a {ScheduleRules.GranularityMinutes}-minute mark" + (allowEndOfDay ? " (24:00 allowed)." : ".")));
        return 0;
    }

    private static void CheckBlock(WorkingHoursBlock block, List<ApiErrorDetail> problems)
    {
        if (problems.Count == 0 && block.EndMinute <= block.StartMinute)
        {
            problems.Add(new ApiErrorDetail("endTime", "The end time must be after the start time."));
        }

        if (!ScheduleRules.IsInSupportedRange(block.EffectiveFrom))
        {
            problems.Add(new ApiErrorDetail("effectiveFrom", ScheduleRules.SupportedRangeMessage));
        }

        if (block.EffectiveTo is { } to && !ScheduleRules.IsInSupportedRange(to))
        {
            problems.Add(new ApiErrorDetail("effectiveTo", ScheduleRules.SupportedRangeMessage));
        }

        if (block.EffectiveTo is { } effectiveTo && effectiveTo < block.EffectiveFrom)
        {
            problems.Add(new ApiErrorDetail("effectiveTo", "effectiveTo cannot be before effectiveFrom."));
        }
    }

    private static void CheckRange(ScheduleException exception, List<ApiErrorDetail> problems)
    {
        if (!ScheduleRules.IsInSupportedRange(exception.StartsAt))
        {
            problems.Add(new ApiErrorDetail("startsAt", ScheduleRules.SupportedRangeMessage));
        }

        if (!ScheduleRules.IsInSupportedRange(exception.EndsAt))
        {
            problems.Add(new ApiErrorDetail("endsAt", ScheduleRules.SupportedRangeMessage));
        }

        if (problems.Count > 0)
        {
            return;
        }

        if (!ScheduleRules.IsOnGranularity(exception.StartsAt))
        {
            problems.Add(new ApiErrorDetail("startsAt", $"Must be on a {ScheduleRules.GranularityMinutes}-minute mark."));
        }

        if (!ScheduleRules.IsOnGranularity(exception.EndsAt))
        {
            problems.Add(new ApiErrorDetail("endsAt", $"Must be on a {ScheduleRules.GranularityMinutes}-minute mark."));
        }

        if (exception.EndsAt <= exception.StartsAt)
        {
            problems.Add(new ApiErrorDetail("endsAt", "endsAt must be after startsAt."));
        }
    }

    /// <summary>Blocks of one veterinarian on one day may not overlap in time while their effective dates overlap.</summary>
    private async Task EnsureNoOverlapAsync(WorkingHoursBlock block, CancellationToken cancellationToken)
    {
        var overlaps = await db.WorkingHours.AnyAsync(
            other => other.Id != block.Id
                && other.VeterinarianId == block.VeterinarianId
                && other.DayOfWeek == block.DayOfWeek
                && other.StartMinute < block.EndMinute && block.StartMinute < other.EndMinute
                && (other.EffectiveTo == null || other.EffectiveTo >= block.EffectiveFrom)
                && (block.EffectiveTo == null || other.EffectiveFrom <= block.EffectiveTo),
            cancellationToken);
        if (overlaps)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                "This block overlaps another block of the same veterinarian on that day.");
        }
    }

    private static void ThrowIfAny(List<ApiErrorDetail> problems)
    {
        if (problems.Count > 0)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.", problems);
        }
    }

    /// <summary>Monday first (ISO), for listing.</summary>
    private static int DayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
