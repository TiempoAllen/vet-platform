using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.Consultations;

/// <summary>
/// Consultations (docs/API.md section 8, ADR-011). Every read and write is clinic-filtered and audited; audit metadata
/// names records and fields, never note text. Writes lock the consultation row (after the clinic-filtered lookup) so
/// concurrent edits, completion and discard are serialised.
/// </summary>
public sealed partial class ConsultationService(
    AppDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor)
{
    // ---------- Reads (audited, SECURITY.md section 10) ----------

    public async Task<PagedResponse<ConsultationResponse>> ListForPatientAsync(
        Guid patientId,
        ConsultationListQuery query,
        CancellationToken cancellationToken)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId, cancellationToken))
        {
            throw NotFound("The patient was not found.");
        }

        var zone = await ZoneAsync(cancellationToken);
        var consultations = WithDetails(db.Consultations.AsNoTracking()).Where(c => c.PatientId == patientId);
        consultations = query.ParsedStatus is { } status
            ? consultations.Where(c => c.Status == status)
            : consultations.Where(c => c.Status != ConsultationStatus.Discarded);

        var page = await consultations.OrderByDescending(c => c.StartedAt).ThenByDescending(c => c.Id)
            .AsSplitQuery()
            .ToPageAsync(query, c => ConsultationResponse.From(c, zone), cancellationToken);

        audit.Record(
            AuditActions.ConsultationsListed, nameof(Patient), patientId,
            new { consultationIds = page.Data.Select(c => c.Id).ToList() });
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    public async Task<ConsultationResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var consultation = await FindAsync(id, tracking: false, cancellationToken);
        audit.Record(AuditActions.ConsultationViewed, nameof(Consultation), id, new { patientId = consultation.PatientId });
        await db.SaveChangesAsync(cancellationToken);
        return ConsultationResponse.From(consultation, zone);
    }

    // ---------- Writes ----------

    public async Task<ConsultationResponse> CreateAsync(CreateConsultationRequest request, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var problems = new List<ApiErrorDetail>();
        CheckVitals(request.WeightKg, request.TemperatureC, request.HeartRate, request.RespiratoryRate,
            request.BodyConditionScore, request.PainScore, problems);

        var patient = await db.Patients.SingleOrDefaultAsync(
            p => p.Id == request.PatientId!.Value && p.Status == PatientStatus.Active, cancellationToken);
        if (patient is null)
        {
            problems.Add(new ApiErrorDetail("patientId", "The patient was not found or is not active."));
        }

        var veterinarian = await ActiveVeterinarianAsync(request.VeterinarianId!.Value, cancellationToken);
        if (veterinarian is null)
        {
            problems.Add(new ApiErrorDetail("veterinarianId", "The veterinarian was not found or is not active."));
        }

        if (request.AppointmentId is { } appointmentId
            && !await db.Appointments.AnyAsync(
                a => a.Id == appointmentId
                    && a.Status == AppointmentStatus.CheckedIn
                    && a.PatientId == request.PatientId!.Value,
                cancellationToken))
        {
            problems.Add(new ApiErrorDetail(
                "appointmentId", "The appointment was not found, is not checked in, or is for another patient."));
        }

        ThrowIfAny(problems);

        var consultation = new Consultation
        {
            ClinicId = CurrentClinicId,
            PatientId = patient!.Id,
            Patient = patient,
            AppointmentId = request.AppointmentId,
            VeterinarianId = veterinarian!.Id,
            Veterinarian = veterinarian,
            StartedAt = Now,
            Subjective = NullIfBlank(request.Subjective),
            Objective = NullIfBlank(request.Objective),
            Assessment = NullIfBlank(request.Assessment),
            Plan = NullIfBlank(request.Plan),
            WeightKg = request.WeightKg,
            TemperatureC = request.TemperatureC,
            HeartRate = request.HeartRate,
            RespiratoryRate = request.RespiratoryRate,
            BodyConditionScore = request.BodyConditionScore,
            PainScore = request.PainScore,
            CreatedByUserId = CallerId,
        };
        db.Consultations.Add(consultation);
        audit.Record(
            AuditActions.ConsultationCreated, nameof(Consultation), consultation.Id,
            new { patientId = consultation.PatientId, appointmentId = consultation.AppointmentId, veterinarianId = consultation.VeterinarianId });
        await SaveAsync(cancellationToken);

        consultation.CreatedBy = await db.Users.SingleOrDefaultAsync(u => u.Id == CallerId, cancellationToken);
        return ConsultationResponse.From(consultation, zone);
    }

    public Task<ConsultationResponse> UpdateAsync(Guid id, UpdateConsultationRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.ConsultationUpdated, async consultation =>
        {
            RequireInProgress(consultation, "edited");
            var changed = new List<string>();
            var problems = new List<ApiErrorDetail>();

            void Set<T>(string field, T current, T wanted, Action<T> apply)
            {
                if (!EqualityComparer<T>.Default.Equals(current, wanted))
                {
                    apply(wanted);
                    changed.Add(field);
                }
            }

            if (request.VeterinarianId is { } vetId && vetId != consultation.VeterinarianId)
            {
                if (await ActiveVeterinarianAsync(vetId, cancellationToken) is { } vet)
                {
                    consultation.VeterinarianId = vet.Id;
                    consultation.Veterinarian = vet;
                    changed.Add("veterinarianId");
                }
                else
                {
                    problems.Add(new ApiErrorDetail("veterinarianId", "The veterinarian was not found or is not active."));
                }
            }

            if (request.Subjective is not null) Set("subjective", consultation.Subjective, NullIfBlank(request.Subjective), v => consultation.Subjective = v);
            if (request.Objective is not null) Set("objective", consultation.Objective, NullIfBlank(request.Objective), v => consultation.Objective = v);
            if (request.Assessment is not null) Set("assessment", consultation.Assessment, NullIfBlank(request.Assessment), v => consultation.Assessment = v);
            if (request.Plan is not null) Set("plan", consultation.Plan, NullIfBlank(request.Plan), v => consultation.Plan = v);
            if (request.WeightKg.IsSet) Set("weightKg", consultation.WeightKg, request.WeightKg.Value, v => consultation.WeightKg = v);
            if (request.TemperatureC.IsSet) Set("temperatureC", consultation.TemperatureC, request.TemperatureC.Value, v => consultation.TemperatureC = v);
            if (request.HeartRate.IsSet) Set("heartRate", consultation.HeartRate, request.HeartRate.Value, v => consultation.HeartRate = v);
            if (request.RespiratoryRate.IsSet) Set("respiratoryRate", consultation.RespiratoryRate, request.RespiratoryRate.Value, v => consultation.RespiratoryRate = v);
            if (request.BodyConditionScore.IsSet) Set("bodyConditionScore", consultation.BodyConditionScore, request.BodyConditionScore.Value, v => consultation.BodyConditionScore = v);
            if (request.PainScore.IsSet) Set("painScore", consultation.PainScore, request.PainScore.Value, v => consultation.PainScore = v);

            CheckVitals(consultation.WeightKg, consultation.TemperatureC, consultation.HeartRate, consultation.RespiratoryRate,
                consultation.BodyConditionScore, consultation.PainScore, problems);
            ThrowIfAny(problems);
            return changed.Count == 0 ? null : (object)new { fields = changed };
        }, cancellationToken);

    /// <summary>
    /// Signs the consultation off (only veterinarians hold <c>consultations.complete</c>). A recorded weight becomes
    /// the patient's current weight in the same transaction.
    /// </summary>
    public Task<ConsultationResponse> CompleteAsync(Guid id, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.ConsultationCompleted, async consultation =>
        {
            RequireInProgress(consultation, "completed");
            var now = Now;
            consultation.Status = ConsultationStatus.Completed;
            consultation.CompletedAt = now < consultation.StartedAt ? consultation.StartedAt : now;
            consultation.CompletedByUserId = CallerId;
            consultation.CompletedBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken);

            var weightUpdated = false;
            if (consultation.WeightKg is { } weight)
            {
                // The patient row is shared with patient edits; lock it like appointments do (after the consultation).
                await db.Database.ExecuteSqlAsync(
                    $"SELECT 1 FROM patients WHERE id = {consultation.PatientId} FOR NO KEY UPDATE", cancellationToken);
                var patient = await db.Patients.SingleAsync(p => p.Id == consultation.PatientId, cancellationToken);
                await db.Entry(patient).ReloadAsync(cancellationToken);
                if (patient.WeightKg != weight)
                {
                    patient.WeightKg = weight;
                    weightUpdated = true;
                    audit.Record(
                        AuditActions.PatientUpdated, nameof(Patient), patient.Id,
                        new { fields = new[] { "weightKg" }, status = patient.Status, sourceConsultationId = consultation.Id });
                }
            }

            return (object?)new { patientId = consultation.PatientId, weightUpdated };
        }, cancellationToken);

    public Task<ConsultationResponse> DiscardAsync(Guid id, DiscardConsultationRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.ConsultationDiscarded, consultation =>
        {
            RequireInProgress(consultation, "discarded");
            consultation.Status = ConsultationStatus.Discarded;
            consultation.DiscardedAt = Now < consultation.StartedAt ? consultation.StartedAt : Now;
            consultation.DiscardReason = request.Reason!.Trim();
            return Task.FromResult<object?>(new { patientId = consultation.PatientId });
        }, cancellationToken);

    /// <summary>Appends a signed addendum to a completed consultation (FR-CLN-2). Addenda are never edited or deleted.</summary>
    public Task<ConsultationResponse> AddAddendumAsync(Guid id, AddAddendumRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.ConsultationAddendumAdded, async consultation =>
        {
            if (consultation.Status != ConsultationStatus.Completed)
            {
                throw Conflict("Addenda can only be added to completed consultations; edit the notes while in progress.");
            }

            if (consultation.Addenda.Count >= ConsultationAddendum.MaxPerConsultation)
            {
                throw Conflict(
                    $"A consultation can have at most {ConsultationAddendum.MaxPerConsultation} addenda; they can never be removed.");
            }

            var addendum = new ConsultationAddendum
            {
                ClinicId = consultation.ClinicId,
                ConsultationId = consultation.Id,
                AuthorUserId = CallerId,
                Author = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken),
                Text = request.Text!.Trim(),
                CreatedAt = Now,
            };
            if (addendum.Text.Length == 0)
            {
                ThrowIfAny([new ApiErrorDetail("text", "The addendum must not be blank.")]);
            }

            consultation.Addenda.Add(addendum);
            db.ConsultationAddenda.Add(addendum);
            return (object?)new { addendumId = addendum.Id };
        }, cancellationToken);

    // ---------- Helpers ----------

    /// <summary>
    /// Locks the consultation row after the clinic-filtered lookup, re-reads it, applies the change and saves it with
    /// its audit entry. <paramref name="apply"/> returns the audit metadata, or null when nothing changed.
    /// </summary>
    private async Task<ConsultationResponse> WriteAsync(
        Guid id,
        string auditAction,
        Func<Consultation, Task<object?>> apply,
        CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Consultations.AsNoTracking().AnyAsync(c => c.Id == id, cancellationToken))
        {
            throw NotFound("The consultation was not found.");
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM consultations WHERE id = {id} FOR NO KEY UPDATE", cancellationToken);
        var consultation = await FindAsync(id, tracking: true, cancellationToken);

        var metadata = await apply(consultation);
        if (metadata is not null)
        {
            audit.Record(auditAction, nameof(Consultation), consultation.Id, metadata);
            await SaveAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return ConsultationResponse.From(consultation, zone);
    }

    private Task<User?> ActiveVeterinarianAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(
            u => u.Id == id && u.Status == UserStatus.Active && u.Roles.Any(r => r.Role == Role.Veterinarian),
            cancellationToken);

    private static void CheckVitals(
        decimal? weightKg,
        decimal? temperatureC,
        int? heartRate,
        int? respiratoryRate,
        int? bodyConditionScore,
        int? painScore,
        List<ApiErrorDetail> problems)
    {
        if (weightKg is { } w && (w <= 0 || w > VitalLimits.MaxWeightKg || decimal.Round(w, 3) != w))
            problems.Add(new ApiErrorDetail("weightKg", $"More than 0 and at most {VitalLimits.MaxWeightKg} kg, with at most 3 decimals."));
        if (temperatureC is { } t && (t < VitalLimits.MinTemperatureC || t > VitalLimits.MaxTemperatureC || decimal.Round(t, 1) != t))
            problems.Add(new ApiErrorDetail("temperatureC", $"Between {VitalLimits.MinTemperatureC} and {VitalLimits.MaxTemperatureC} °C, with at most 1 decimal."));
        if (heartRate is { } h && (h < 1 || h > VitalLimits.MaxHeartRate))
            problems.Add(new ApiErrorDetail("heartRate", $"Between 1 and {VitalLimits.MaxHeartRate} beats per minute."));
        if (respiratoryRate is { } r && (r < 1 || r > VitalLimits.MaxRespiratoryRate))
            problems.Add(new ApiErrorDetail("respiratoryRate", $"Between 1 and {VitalLimits.MaxRespiratoryRate} breaths per minute."));
        if (bodyConditionScore is { } b && (b < 1 || b > 9))
            problems.Add(new ApiErrorDetail("bodyConditionScore", "Between 1 and 9."));
        if (painScore is { } p && (p < 0 || p > 10))
            problems.Add(new ApiErrorDetail("painScore", "Between 0 and 10."));
    }

    private static void RequireInProgress(Consultation consultation, string verb)
    {
        if (consultation.Status != ConsultationStatus.InProgress)
        {
            throw Conflict($"A {consultation.Status} consultation cannot be {verb}.");
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique
                  && unique.ConstraintName is Consultation.OneOpenPerPatientIndex or Consultation.OnePerAppointmentIndex
                      or Diagnosis.OnePrimaryIndex)
        {
            throw unique.ConstraintName switch
            {
                Consultation.OneOpenPerPatientIndex =>
                    Conflict("The patient already has a consultation in progress; complete or discard it first."),
                Consultation.OnePerAppointmentIndex => Conflict("The appointment already has a consultation."),
                _ => Conflict("Another diagnosis of this consultation is already primary."),
            };
        }
    }

    private Guid CurrentClinicId =>
        tenant.ClinicId ?? throw new InvalidOperationException("Consultations require a current clinic.");

    /// <summary>The clock at database precision, so a write's response equals a later read (see TimestampsInterceptor).</summary>
    private DateTimeOffset Now => TimestampsInterceptor.ToDatabasePrecision(timeProvider.GetUtcNow());

    private Guid CallerId =>
        AuthClaims.GetUserId(httpContextAccessor.HttpContext!.User)
        ?? throw new InvalidOperationException("Consultations require a signed-in user.");

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) =>
        ClinicTimeZone.Of(await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken));

    private static IQueryable<Consultation> WithDetails(IQueryable<Consultation> consultations) =>
        consultations
            .Include(c => c.Patient)
            .Include(c => c.Veterinarian)
            .Include(c => c.CompletedBy)
            .Include(c => c.CreatedBy)
            .Include(c => c.Addenda).ThenInclude(a => a.Author)
            .Include(c => c.Diagnoses)
            .Include(c => c.Treatments);

    private async Task<Consultation> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var consultations = WithDetails(tracking ? db.Consultations : db.Consultations.AsNoTracking());
        return await consultations.AsSplitQuery().SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw NotFound("The consultation was not found.");
    }

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
