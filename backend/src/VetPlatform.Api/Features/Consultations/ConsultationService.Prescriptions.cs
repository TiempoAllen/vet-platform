using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.Consultations;

/// <summary>
/// Prescriptions of a consultation (docs/API.md section 9, ADR-013). The controller requires
/// <c>prescriptions.issue</c> (veterinarians). Every change goes through <see cref="WriteAsync"/>, so it runs under the
/// consultation's row lock and is audited (ids, field names and statuses, never medication names, instructions or
/// reasons). Editable while the consultation is in progress; once completed a prescription can only be cancelled. A
/// database trigger enforces the same rule.
/// </summary>
public sealed partial class ConsultationService
{
    public Task<ConsultationResponse> AddPrescriptionAsync(Guid id, CreatePrescriptionRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.PrescriptionAdded, async consultation =>
        {
            RequireInProgress(consultation, "changed");
            if (consultation.Prescriptions.Count >= Prescription.MaxPerConsultation)
            {
                throw Conflict($"A consultation can have at most {Prescription.MaxPerConsultation} prescriptions.");
            }

            var problems = new List<ApiErrorDetail>();
            CheckQuantity(request.Quantity!.Value, problems);
            CheckRefills(request.Refills ?? 0, problems);
            ThrowIfAny(problems);
            CheckDiagnosisLink(consultation, request.DiagnosisId);

            var prescription = new Prescription
            {
                ClinicId = consultation.ClinicId,
                ConsultationId = consultation.Id,
                PatientId = consultation.PatientId,
                DiagnosisId = request.DiagnosisId,
                MedicationName = request.MedicationName!.Trim(),
                Dose = request.Dose!.Trim(),
                Route = NullIfBlank(request.Route),
                Frequency = request.Frequency!.Trim(),
                Duration = NullIfBlank(request.Duration),
                Quantity = request.Quantity.Value,
                QuantityUnit = request.QuantityUnit!.Trim(),
                Refills = request.Refills ?? 0,
                Instructions = NullIfBlank(request.Instructions),
                PrescribedByUserId = CallerId,
                PrescribedBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken),
            };
            consultation.Prescriptions.Add(prescription);
            db.Prescriptions.Add(prescription);
            return (object?)new { prescriptionId = prescription.Id, diagnosisId = prescription.DiagnosisId };
        }, cancellationToken);

    public Task<ConsultationResponse> UpdatePrescriptionAsync(
        Guid id,
        Guid prescriptionId,
        UpdatePrescriptionRequest request,
        CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.PrescriptionUpdated, consultation =>
        {
            var prescription = FindPrescription(consultation, prescriptionId);
            if (consultation.Status == ConsultationStatus.Completed)
            {
                throw Conflict("The consultation is completed: the prescription can no longer be edited, only cancelled.");
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

            // Required text may be replaced but never cleared.
            void SetRequired(string field, string? value, string current, Action<string> apply)
            {
                if (value is null)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    problems.Add(new ApiErrorDetail(field, "This field must not be blank."));
                    return;
                }

                Set(field, current, value.Trim(), apply);
            }

            SetRequired("medicationName", request.MedicationName, prescription.MedicationName, v => prescription.MedicationName = v);
            SetRequired("dose", request.Dose, prescription.Dose, v => prescription.Dose = v);
            SetRequired("frequency", request.Frequency, prescription.Frequency, v => prescription.Frequency = v);
            SetRequired("quantityUnit", request.QuantityUnit, prescription.QuantityUnit, v => prescription.QuantityUnit = v);
            if (request.Route is not null) Set("route", prescription.Route, NullIfBlank(request.Route), v => prescription.Route = v);
            if (request.Duration is not null) Set("duration", prescription.Duration, NullIfBlank(request.Duration), v => prescription.Duration = v);
            if (request.Instructions is not null) Set("instructions", prescription.Instructions, NullIfBlank(request.Instructions), v => prescription.Instructions = v);
            if (request.Quantity is { } quantity)
            {
                CheckQuantity(quantity, problems);
                Set("quantity", prescription.Quantity, quantity, v => prescription.Quantity = v);
            }

            if (request.Refills is { } refills)
            {
                CheckRefills(refills, problems);
                Set("refills", prescription.Refills, refills, v => prescription.Refills = v);
            }

            ThrowIfAny(problems);
            if (request.DiagnosisId.IsSet)
            {
                CheckDiagnosisLink(consultation, request.DiagnosisId.Value);
                Set("diagnosisId", prescription.DiagnosisId, request.DiagnosisId.Value, v => prescription.DiagnosisId = v);
            }

            return Task.FromResult<object?>(changed.Count == 0 ? null : new { prescriptionId, fields = changed });
        }, cancellationToken);

    public Task<ConsultationResponse> RemovePrescriptionAsync(Guid id, Guid prescriptionId, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.PrescriptionRemoved, consultation =>
        {
            var prescription = FindPrescription(consultation, prescriptionId);
            if (consultation.Status == ConsultationStatus.Completed)
            {
                throw Conflict("The consultation is completed: the prescription can no longer be removed, only cancelled.");
            }

            RequireInProgress(consultation, "changed");
            consultation.Prescriptions.Remove(prescription);
            db.Prescriptions.Remove(prescription);
            return Task.FromResult<object?>(new { prescriptionId });
        }, cancellationToken);

    /// <summary>Stops a prescription of a completed consultation. The row stays, with who cancelled it, when and why.</summary>
    public Task<ConsultationResponse> CancelPrescriptionAsync(
        Guid id,
        Guid prescriptionId,
        CancelPrescriptionRequest request,
        CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.PrescriptionCancelled, async consultation =>
        {
            var prescription = FindPrescription(consultation, prescriptionId);
            if (consultation.Status != ConsultationStatus.Completed)
            {
                throw Conflict(consultation.Status == ConsultationStatus.InProgress
                    ? "Only prescriptions of completed consultations can be cancelled; remove or edit it instead."
                    : $"A prescription of a {consultation.Status} consultation cannot be cancelled.");
            }

            if (prescription.Status == PrescriptionStatus.Cancelled)
            {
                throw Conflict("The prescription is already cancelled.");
            }

            prescription.Status = PrescriptionStatus.Cancelled;
            prescription.CancelledAt = Now;
            prescription.CancelledByUserId = CallerId;
            prescription.CancelledBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken);
            prescription.CancelReason = request.Reason!.Trim();
            return (object?)new { prescriptionId, from = PrescriptionStatus.Active, to = PrescriptionStatus.Cancelled };
        }, cancellationToken);

    /// <summary>
    /// The patient's medication list: prescriptions of completed consultations, newest consultation first (in the order
    /// recorded within one). A clinical read: audited.
    /// </summary>
    public async Task<PagedResponse<PatientPrescriptionResponse>> ListPatientPrescriptionsAsync(
        Guid patientId,
        PatientPrescriptionListQuery query,
        CancellationToken cancellationToken)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId, cancellationToken))
        {
            throw NotFound("The patient was not found.");
        }

        var zone = await ZoneAsync(cancellationToken);
        var rows =
            from p in db.Prescriptions.AsNoTracking()
            join c in db.Consultations.AsNoTracking() on new { p.ClinicId, p.ConsultationId } equals new { c.ClinicId, ConsultationId = c.Id }
            where p.PatientId == patientId && c.Status == ConsultationStatus.Completed
            select new
            {
                Prescription = p,
                p.PrescribedBy,
                p.CancelledBy,
                c.CompletedAt,
                c.VeterinarianId,
                VeterinarianName = c.Veterinarian!.Name,
            };
        if (query.ParsedStatus is { } status)
        {
            rows = rows.Where(r => r.Prescription.Status == status);
        }

        var page = await rows
            .OrderByDescending(r => r.CompletedAt)
            .ThenByDescending(r => r.Prescription.ConsultationId) // keeps one consultation's prescriptions together on ties
            .ThenBy(r => r.Prescription.CreatedAt)
            .ThenBy(r => r.Prescription.Id)
            .ToPageAsync(
                query,
                r =>
                {
                    r.Prescription.PrescribedBy = r.PrescribedBy;
                    r.Prescription.CancelledBy = r.CancelledBy;
                    return new PatientPrescriptionResponse(
                        PrescriptionResponse.From(r.Prescription, zone),
                        r.Prescription.ConsultationId,
                        TimeZoneInfo.ConvertTime(r.CompletedAt!.Value, zone),
                        new AppointmentPersonSummary(r.VeterinarianId, r.VeterinarianName));
                },
                cancellationToken);

        audit.Record(
            AuditActions.PrescriptionsListed, nameof(Patient), patientId,
            new { prescriptionIds = page.Data.Select(r => r.Prescription.Id).ToList() });
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    private static Prescription FindPrescription(Consultation consultation, Guid prescriptionId) =>
        consultation.Prescriptions.SingleOrDefault(p => p.Id == prescriptionId)
        ?? throw NotFound("The prescription was not found.");

    private static void CheckQuantity(decimal quantity, List<ApiErrorDetail> problems)
    {
        if (quantity <= 0 || quantity > Prescription.MaxQuantity || decimal.Round(quantity, 3) != quantity)
        {
            problems.Add(new ApiErrorDetail(
                "quantity", $"More than 0 and at most {Prescription.MaxQuantity}, with at most 3 decimals."));
        }
    }

    private static void CheckRefills(int refills, List<ApiErrorDetail> problems)
    {
        if (refills is < 0 or > Prescription.MaxRefills)
        {
            problems.Add(new ApiErrorDetail("refills", $"Between 0 and {Prescription.MaxRefills}."));
        }
    }
}
