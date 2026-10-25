using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Consultations;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;
using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Features.Vaccinations;

/// <summary>
/// Vaccinations (docs/API.md section 9, ADR-014). Records are never changed once saved; a wrong one is voided with a
/// reason. Every read and write is clinic-filtered and audited; audit metadata names records, never vaccine names,
/// notes or reasons.
/// </summary>
public sealed class VaccinationService(
    AppDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor)
{
    // ---------- Reads ----------

    /// <summary>The patient's vaccinations, newest dose first, voided ones included unless filtered. Audited.</summary>
    public async Task<PagedResponse<VaccinationResponse>> ListForPatientAsync(
        Guid patientId,
        VaccinationListQuery query,
        CancellationToken cancellationToken)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId, cancellationToken))
        {
            throw NotFound("The patient was not found.");
        }

        var zone = await ZoneAsync(cancellationToken);
        var vaccinations = WithPeople(db.Vaccinations.AsNoTracking()).Where(v => v.PatientId == patientId);
        if (query.ParsedStatus is { } status)
        {
            vaccinations = vaccinations.Where(v => v.Status == status);
        }

        var page = await vaccinations
            .OrderByDescending(v => v.AdministeredOn).ThenByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id)
            .ToPageAsync(query, v => VaccinationResponse.From(v, zone), cancellationToken);

        audit.Record(
            AuditActions.VaccinationsListed, nameof(Patient), patientId,
            new { vaccinationIds = page.Data.Select(v => v.Id).ToList() });
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    public async Task<VaccinationResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var vaccination = await WithPeople(db.Vaccinations.AsNoTracking()).SingleOrDefaultAsync(v => v.Id == id, cancellationToken)
            ?? throw NotFound("The vaccination was not found.");
        audit.Record(AuditActions.VaccinationViewed, nameof(Vaccination), id, new { patientId = vaccination.PatientId });
        await db.SaveChangesAsync(cancellationToken);
        return VaccinationResponse.From(vaccination, zone);
    }

    /// <summary>
    /// The clinic's due list: for each active patient and vaccine (names compared ignoring case), the latest active
    /// dose, if its next dose is due in the range. A newer dose without a due date ends the series. Audited.
    /// </summary>
    public async Task<PagedResponse<DueVaccinationResponse>> ListDueAsync(VaccinationDueQuery query, CancellationToken cancellationToken)
    {
        var clinicId = CurrentClinicId;
        var (from, to) = (query.ParsedFrom, query.ParsedTo);

        // DISTINCT ON keeps the latest active dose per patient and vaccine; the rest composes over it in SQL.
        var latest = db.Vaccinations.FromSql($"""
            SELECT DISTINCT ON (patient_id, lower(vaccine_name)) *
            FROM vaccinations
            WHERE clinic_id = {clinicId} AND status = 'Active'
            ORDER BY patient_id, lower(vaccine_name), administered_on DESC, created_at DESC, id DESC
            """);

        var due = latest.AsNoTracking()
            .Where(v => v.NextDueOn != null && v.NextDueOn <= to && v.Patient!.Status == PatientStatus.Active);
        if (from is { } start)
        {
            due = due.Where(v => v.NextDueOn >= start);
        }

        var page = await due
            .OrderBy(v => v.NextDueOn).ThenBy(v => v.Patient!.Name).ThenBy(v => v.Id)
            .Select(v => new
            {
                v.Id,
                v.VaccineName,
                v.AdministeredOn,
                v.NextDueOn,
                PatientId = v.Patient!.Id,
                PatientName = v.Patient.Name,
                v.Patient.Species,
                v.Patient.Client,
            })
            .ToPageAsync(
                query,
                r => new DueVaccinationResponse(
                    r.Id, r.VaccineName, r.AdministeredOn, r.NextDueOn!.Value,
                    new AppointmentPatientSummary(r.PatientId, r.PatientName, r.Species),
                    r.Client is { } c
                        ? new DueVaccinationClient(c.Id, c.FirstName, c.LastName, c.Phone, c.Email, c.PreferredContactMethod)
                        : null),
                cancellationToken);

        audit.Record(
            AuditActions.VaccinationsDueListed, nameof(Clinic), clinicId,
            new { from, to, vaccinationIds = page.Data.Select(v => v.VaccinationId).ToList() });
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    // ---------- Writes ----------

    public async Task<VaccinationResponse> RecordAsync(
        Guid patientId,
        RecordVaccinationRequest request,
        CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken)
            ?? throw NotFound("The patient was not found.");
        if (patient.Status != PatientStatus.Active)
        {
            throw Conflict($"Vaccinations can only be recorded for active patients; this one is {patient.Status}.");
        }

        var problems = new List<ApiErrorDetail>();
        var administeredOn = request.AdministeredOn!.Value;
        if (!ScheduleRules.IsInSupportedRange(administeredOn) || administeredOn > today)
        {
            problems.Add(new ApiErrorDetail("administeredOn", "Must be a date from 2000-01-01 up to today (clinic time)."));
        }

        if (request.NextDueOn is { } nextDue && (nextDue <= administeredOn || !ScheduleRules.IsInSupportedRange(nextDue)))
        {
            problems.Add(new ApiErrorDetail("nextDueOn", "Must be after the administered date and no later than 2100-12-31."));
        }

        if (request.LotExpiresOn is { } expires && !ScheduleRules.IsInSupportedRange(expires))
        {
            problems.Add(new ApiErrorDetail("lotExpiresOn", ScheduleRules.SupportedRangeMessage));
        }

        var elsewhere = NullIfBlank(request.AdministeredElsewhere);
        User? veterinarian = null;
        if (elsewhere is not null)
        {
            if (request.VeterinarianId is not null)
            {
                problems.Add(new ApiErrorDetail("veterinarianId", "Leave it out for a dose given elsewhere."));
            }
        }
        else if (request.VeterinarianId is not { } vetId)
        {
            problems.Add(new ApiErrorDetail("veterinarianId", "Required for a dose given here (or say where else it was given)."));
        }
        else if ((veterinarian = await ActiveVeterinarianAsync(vetId, cancellationToken)) is null)
        {
            problems.Add(new ApiErrorDetail("veterinarianId", "The veterinarian was not found or is not active."));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (request.ConsultationId is { } consultationId)
        {
            // Lock the consultation (after the clinic-filtered lookup) so a concurrent discard cannot slip in between
            // this check and the insert.
            var linkable = await db.Consultations.AsNoTracking().AnyAsync(
                c => c.Id == consultationId && c.PatientId == patientId, cancellationToken);
            if (linkable)
            {
                await db.Database.ExecuteSqlAsync(
                    $"SELECT 1 FROM consultations WHERE id = {consultationId} FOR SHARE", cancellationToken);
                linkable = await db.Consultations.AsNoTracking().AnyAsync(
                    c => c.Id == consultationId && c.Status != ConsultationStatus.Discarded, cancellationToken);
            }

            if (!linkable)
            {
                problems.Add(new ApiErrorDetail(
                    "consultationId", "The consultation was not found, is discarded, or is for another patient."));
            }
        }

        ThrowIfAny(problems);

        var vaccination = new Vaccination
        {
            ClinicId = CurrentClinicId,
            PatientId = patient.Id,
            ConsultationId = request.ConsultationId,
            VaccineName = request.VaccineName!.Trim(),
            Manufacturer = NullIfBlank(request.Manufacturer),
            LotNumber = NullIfBlank(request.LotNumber),
            LotExpiresOn = request.LotExpiresOn,
            AdministeredOn = administeredOn,
            AdministeredDayStartsAt = WorkingHoursCalculator.ToInstant(administeredOn.ToDateTime(TimeOnly.MinValue), zone),
            NextDueOn = request.NextDueOn,
            AdministeredElsewhere = elsewhere,
            VeterinarianId = veterinarian?.Id,
            Veterinarian = veterinarian,
            Notes = NullIfBlank(request.Notes),
            RecordedByUserId = CallerId,
            RecordedBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken),
        };
        db.Vaccinations.Add(vaccination);
        audit.Record(
            AuditActions.VaccinationRecorded, nameof(Vaccination), vaccination.Id,
            new
            {
                patientId = vaccination.PatientId,
                consultationId = vaccination.ConsultationId,
                veterinarianId = vaccination.VeterinarianId,
                administeredElsewhere = elsewhere is not null,
            });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return VaccinationResponse.From(vaccination, zone);
    }

    /// <summary>Marks a record as entered in error. The row stays, with who voided it, when and why.</summary>
    public async Task<VaccinationResponse> VoidAsync(Guid id, VoidVaccinationRequest request, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Vaccinations.AsNoTracking().AnyAsync(v => v.Id == id, cancellationToken))
        {
            throw NotFound("The vaccination was not found.");
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM vaccinations WHERE id = {id} FOR NO KEY UPDATE", cancellationToken);
        var vaccination = await WithPeople(db.Vaccinations).SingleAsync(v => v.Id == id, cancellationToken);
        if (vaccination.Status == VaccinationStatus.Voided)
        {
            throw Conflict("The vaccination is already voided.");
        }

        vaccination.Status = VaccinationStatus.Voided;
        vaccination.VoidedAt = TimestampsInterceptor.ToDatabasePrecision(timeProvider.GetUtcNow());
        vaccination.VoidedByUserId = CallerId;
        vaccination.VoidedBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken);
        vaccination.VoidReason = request.Reason!.Trim();
        audit.Record(
            AuditActions.VaccinationVoided, nameof(Vaccination), id,
            new { patientId = vaccination.PatientId, from = VaccinationStatus.Active, to = VaccinationStatus.Voided });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return VaccinationResponse.From(vaccination, zone);
    }

    // ---------- Helpers ----------

    private static IQueryable<Vaccination> WithPeople(IQueryable<Vaccination> vaccinations) =>
        vaccinations.Include(v => v.Veterinarian).Include(v => v.VoidedBy).Include(v => v.RecordedBy);

    private Task<User?> ActiveVeterinarianAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(
            u => u.Id == id && u.Status == UserStatus.Active && u.Roles.Any(r => r.Role == Role.Veterinarian),
            cancellationToken);

    private Guid CurrentClinicId =>
        tenant.ClinicId ?? throw new InvalidOperationException("Vaccinations require a current clinic.");

    private Guid CallerId =>
        AuthClaims.GetUserId(httpContextAccessor.HttpContext!.User)
        ?? throw new InvalidOperationException("Vaccinations require a signed-in user.");

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) =>
        ClinicTimeZone.Of(await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken));

    private static void ThrowIfAny(List<ApiErrorDetail> problems)
    {
        if (problems.Count > 0)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.", problems);
        }
    }

    private static ApiException NotFound(string message) => new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);

    private static ApiException Conflict(string message) => new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, message);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
