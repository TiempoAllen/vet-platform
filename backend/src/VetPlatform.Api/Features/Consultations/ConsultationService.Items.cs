using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.Consultations;

/// <summary>
/// Diagnoses and treatments of a consultation (docs/API.md section 9, ADR-012). Every change goes through
/// <see cref="WriteAsync"/>, so it runs under the consultation's row lock and is audited (ids and field names, never
/// clinical text). While the consultation is in progress everything is editable; once completed only a diagnosis's
/// status may change, by a veterinarian. A database trigger enforces the same rule.
/// </summary>
public sealed partial class ConsultationService
{
    // ---------- Diagnoses ----------

    public Task<ConsultationResponse> AddDiagnosisAsync(Guid id, CreateDiagnosisRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.DiagnosisAdded, async consultation =>
        {
            RequireInProgress(consultation, "changed");
            if (consultation.Diagnoses.Count >= Diagnosis.MaxPerConsultation)
            {
                throw Conflict($"A consultation can have at most {Diagnosis.MaxPerConsultation} diagnoses.");
            }

            var problems = new List<ApiErrorDetail>();
            var (code, codeSystem) = (NullIfBlank(request.Code), NullIfBlank(request.CodeSystem));
            CheckCode(code, codeSystem, problems);
            ThrowIfAny(problems);

            var diagnosis = new Diagnosis
            {
                ClinicId = consultation.ClinicId,
                ConsultationId = consultation.Id,
                PatientId = consultation.PatientId,
                Name = request.Name!.Trim(),
                Code = code,
                CodeSystem = codeSystem,
                Status = request.Status ?? DiagnosisStatus.Provisional,
                Notes = NullIfBlank(request.Notes),
                CreatedByUserId = CallerId,
            };
            if (request.IsPrimary == true)
            {
                await ClearOtherPrimaryAsync(consultation, diagnosis, cancellationToken);
                diagnosis.IsPrimary = true;
            }

            consultation.Diagnoses.Add(diagnosis);
            db.Diagnoses.Add(diagnosis);
            return (object?)new { diagnosisId = diagnosis.Id, status = diagnosis.Status, isPrimary = diagnosis.IsPrimary };
        }, cancellationToken);

    public Task<ConsultationResponse> UpdateDiagnosisAsync(
        Guid id,
        Guid diagnosisId,
        UpdateDiagnosisRequest request,
        CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.DiagnosisUpdated, async consultation =>
        {
            var diagnosis = consultation.Diagnoses.SingleOrDefault(d => d.Id == diagnosisId)
                ?? throw NotFound("The diagnosis was not found.");
            var before = diagnosis.Status;

            if (consultation.Status == ConsultationStatus.Completed)
            {
                // After sign-off only the certainty may change, and that is a veterinarian's judgement.
                if (request.Name is not null || request.Code is not null || request.CodeSystem is not null
                    || request.IsPrimary is not null || request.Notes is not null)
                {
                    throw Conflict("The consultation is completed: only a diagnosis's status can still change.");
                }

                if (!Permissions.Grant(Permissions.RolesOf(httpContextAccessor.HttpContext!.User), Permissions.ConsultationsComplete))
                {
                    throw new ApiException(
                        StatusCodes.Status403Forbidden, ErrorCodes.Forbidden,
                        "Only a veterinarian can change a diagnosis after the consultation is completed.");
                }

                if (request.Status is { } status && status != diagnosis.Status)
                {
                    diagnosis.Status = status;
                    return (object?)new { diagnosisId, fields = new[] { "status" }, from = before, to = status, afterCompletion = true };
                }

                return null;
            }

            RequireInProgress(consultation, "changed");
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

            if (request.Name is { } name)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    problems.Add(new ApiErrorDetail("name", "The name must not be blank."));
                }
                else
                {
                    Set("name", diagnosis.Name, name.Trim(), v => diagnosis.Name = v);
                }
            }

            if (request.Code is not null) Set("code", diagnosis.Code, NullIfBlank(request.Code), v => diagnosis.Code = v);
            if (request.CodeSystem is not null) Set("codeSystem", diagnosis.CodeSystem, NullIfBlank(request.CodeSystem), v => diagnosis.CodeSystem = v);
            if (request.Notes is not null) Set("notes", diagnosis.Notes, NullIfBlank(request.Notes), v => diagnosis.Notes = v);
            if (request.Status is { } newStatus) Set("status", diagnosis.Status, newStatus, v => diagnosis.Status = v);

            // Validate before the primary swap: its intermediate save would otherwise flush invalid values (a 500).
            CheckCode(diagnosis.Code, diagnosis.CodeSystem, problems);
            ThrowIfAny(problems);

            if (request.IsPrimary is { } primary && primary != diagnosis.IsPrimary)
            {
                if (primary)
                {
                    await ClearOtherPrimaryAsync(consultation, diagnosis, cancellationToken);
                    diagnosis.IsPrimary = true;
                }
                else
                {
                    diagnosis.IsPrimary = false;
                }

                changed.Add("isPrimary");
            }
            return changed.Count == 0 ? null : (object)new { diagnosisId, fields = changed, from = before, to = diagnosis.Status };
        }, cancellationToken);

    /// <summary>
    /// Removes a diagnosis of an in-progress consultation; treatments and prescriptions linked to it are unlinked, not
    /// removed.
    /// </summary>
    public Task<ConsultationResponse> RemoveDiagnosisAsync(Guid id, Guid diagnosisId, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.DiagnosisRemoved, consultation =>
        {
            var diagnosis = consultation.Diagnoses.SingleOrDefault(d => d.Id == diagnosisId)
                ?? throw NotFound("The diagnosis was not found.");
            RequireInProgress(consultation, "changed");
            var unlinked = consultation.Treatments.Where(t => t.DiagnosisId == diagnosisId).ToList();
            foreach (var treatment in unlinked)
            {
                treatment.DiagnosisId = null;
            }

            var unlinkedPrescriptions = consultation.Prescriptions.Where(p => p.DiagnosisId == diagnosisId).ToList();
            foreach (var prescription in unlinkedPrescriptions)
            {
                prescription.DiagnosisId = null;
            }

            consultation.Diagnoses.Remove(diagnosis);
            db.Diagnoses.Remove(diagnosis);
            return Task.FromResult<object?>(new
            {
                diagnosisId,
                unlinkedTreatmentIds = unlinked.Select(t => t.Id).ToList(),
                unlinkedPrescriptionIds = unlinkedPrescriptions.Select(p => p.Id).ToList(),
            });
        }, cancellationToken);

    // ---------- Treatments ----------

    public Task<ConsultationResponse> AddTreatmentAsync(Guid id, CreateTreatmentRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.TreatmentAdded, consultation =>
        {
            RequireInProgress(consultation, "changed");
            if (consultation.Treatments.Count >= Treatment.MaxPerConsultation)
            {
                throw Conflict($"A consultation can have at most {Treatment.MaxPerConsultation} treatments.");
            }

            CheckDiagnosisLink(consultation, request.DiagnosisId);
            var treatment = new Treatment
            {
                ClinicId = consultation.ClinicId,
                ConsultationId = consultation.Id,
                PatientId = consultation.PatientId,
                DiagnosisId = request.DiagnosisId,
                Name = request.Name!.Trim(),
                Type = request.Type!.Value,
                Dose = NullIfBlank(request.Dose),
                Route = NullIfBlank(request.Route),
                Frequency = NullIfBlank(request.Frequency),
                Duration = NullIfBlank(request.Duration),
                Notes = NullIfBlank(request.Notes),
                CreatedByUserId = CallerId,
            };
            consultation.Treatments.Add(treatment);
            db.Treatments.Add(treatment);
            return Task.FromResult<object?>(new { treatmentId = treatment.Id, type = treatment.Type, diagnosisId = treatment.DiagnosisId });
        }, cancellationToken);

    public Task<ConsultationResponse> UpdateTreatmentAsync(
        Guid id,
        Guid treatmentId,
        UpdateTreatmentRequest request,
        CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.TreatmentUpdated, consultation =>
        {
            var treatment = consultation.Treatments.SingleOrDefault(t => t.Id == treatmentId)
                ?? throw NotFound("The treatment was not found.");
            RequireInProgress(consultation, "changed");
            var changed = new List<string>();

            void Set<T>(string field, T current, T wanted, Action<T> apply)
            {
                if (!EqualityComparer<T>.Default.Equals(current, wanted))
                {
                    apply(wanted);
                    changed.Add(field);
                }
            }

            if (request.Name is { } name)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    ThrowIfAny([new ApiErrorDetail("name", "The name must not be blank.")]);
                }

                Set("name", treatment.Name, name.Trim(), v => treatment.Name = v);
            }

            if (request.Type is { } type) Set("type", treatment.Type, type, v => treatment.Type = v);
            if (request.DiagnosisId.IsSet)
            {
                CheckDiagnosisLink(consultation, request.DiagnosisId.Value);
                Set("diagnosisId", treatment.DiagnosisId, request.DiagnosisId.Value, v => treatment.DiagnosisId = v);
            }

            if (request.Dose is not null) Set("dose", treatment.Dose, NullIfBlank(request.Dose), v => treatment.Dose = v);
            if (request.Route is not null) Set("route", treatment.Route, NullIfBlank(request.Route), v => treatment.Route = v);
            if (request.Frequency is not null) Set("frequency", treatment.Frequency, NullIfBlank(request.Frequency), v => treatment.Frequency = v);
            if (request.Duration is not null) Set("duration", treatment.Duration, NullIfBlank(request.Duration), v => treatment.Duration = v);
            if (request.Notes is not null) Set("notes", treatment.Notes, NullIfBlank(request.Notes), v => treatment.Notes = v);

            return Task.FromResult<object?>(changed.Count == 0 ? null : new { treatmentId, fields = changed });
        }, cancellationToken);

    public Task<ConsultationResponse> RemoveTreatmentAsync(Guid id, Guid treatmentId, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.TreatmentRemoved, consultation =>
        {
            var treatment = consultation.Treatments.SingleOrDefault(t => t.Id == treatmentId)
                ?? throw NotFound("The treatment was not found.");
            RequireInProgress(consultation, "changed");
            consultation.Treatments.Remove(treatment);
            db.Treatments.Remove(treatment);
            return Task.FromResult<object?>(new { treatmentId });
        }, cancellationToken);

    // ---------- Problem list ----------

    /// <summary>
    /// The patient's diagnoses from completed consultations, newest consultation first (primary first within one).
    /// A clinical read: audited.
    /// </summary>
    public async Task<PagedResponse<PatientDiagnosisResponse>> ListPatientDiagnosesAsync(
        Guid patientId,
        PatientDiagnosisListQuery query,
        CancellationToken cancellationToken)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId, cancellationToken))
        {
            throw NotFound("The patient was not found.");
        }

        var zone = await ZoneAsync(cancellationToken);
        var rows =
            from d in db.Diagnoses.AsNoTracking()
            join c in db.Consultations.AsNoTracking() on new { d.ClinicId, d.ConsultationId } equals new { c.ClinicId, ConsultationId = c.Id }
            where d.PatientId == patientId && c.Status == ConsultationStatus.Completed
            select new { Diagnosis = d, c.CompletedAt, VeterinarianId = c.VeterinarianId, VeterinarianName = c.Veterinarian!.Name };
        if (query.ParsedStatus is { } status)
        {
            rows = rows.Where(r => r.Diagnosis.Status == status);
        }

        var page = await rows
            .OrderByDescending(r => r.CompletedAt)
            .ThenByDescending(r => r.Diagnosis.ConsultationId) // keeps one consultation's diagnoses together on ties
            .ThenByDescending(r => r.Diagnosis.IsPrimary)
            .ThenBy(r => r.Diagnosis.CreatedAt)
            .ThenBy(r => r.Diagnosis.Id)
            .ToPageAsync(
                query,
                r => new PatientDiagnosisResponse(
                    DiagnosisResponse.From(r.Diagnosis),
                    r.Diagnosis.ConsultationId,
                    TimeZoneInfo.ConvertTime(r.CompletedAt!.Value, zone),
                    new AppointmentPersonSummary(r.VeterinarianId, r.VeterinarianName)),
                cancellationToken);

        audit.Record(
            AuditActions.DiagnosesListed, nameof(Patient), patientId,
            new { diagnosisIds = page.Data.Select(r => r.Diagnosis.Id).ToList() });
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    // ---------- Helpers ----------

    /// <summary>
    /// Only one primary diagnosis per consultation: making one primary clears the flag on the others first. Saved
    /// separately (inside the same transaction) because PostgreSQL checks the unique index row by row, so the old
    /// primary must be cleared before the new one is set.
    /// </summary>
    private async Task ClearOtherPrimaryAsync(Consultation consultation, Diagnosis diagnosis, CancellationToken cancellationToken)
    {
        var others = consultation.Diagnoses.Where(d => d.IsPrimary && d.Id != diagnosis.Id).ToList();
        if (others.Count == 0)
        {
            return;
        }

        foreach (var other in others)
        {
            other.IsPrimary = false;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void CheckCode(string? code, string? codeSystem, List<ApiErrorDetail> problems)
    {
        if (codeSystem is not null && code is null)
        {
            problems.Add(new ApiErrorDetail("codeSystem", "A code system needs a code."));
        }
    }

    private static void CheckDiagnosisLink(Consultation consultation, Guid? diagnosisId)
    {
        if (diagnosisId is { } id && consultation.Diagnoses.All(d => d.Id != id))
        {
            ThrowIfAny([new ApiErrorDetail("diagnosisId", "The diagnosis is not part of this consultation.")]);
        }
    }
}
