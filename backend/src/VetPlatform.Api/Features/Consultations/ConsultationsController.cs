using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Consultations;

/// <summary>
/// Consultations (docs/API.md section 8, ADR-011). Reads need <c>clinical.read</c> and are audited; writes need
/// <c>clinical.write</c>; completion needs <c>consultations.complete</c> (veterinarians). There is no delete.
/// </summary>
[ApiController]
public sealed class ConsultationsController(ConsultationService consultations) : ControllerBase
{
    [HttpGet("api/patients/{patientId:guid}/consultations")]
    [RequirePermission(Permissions.ClinicalRead)]
    public Task<PagedResponse<ConsultationResponse>> ListForPatient(
        Guid patientId,
        [FromQuery] ConsultationListQuery query,
        CancellationToken cancellationToken) =>
        consultations.ListForPatientAsync(patientId, query, cancellationToken);

    [HttpGet("api/consultations/{id:guid}")]
    [RequirePermission(Permissions.ClinicalRead)]
    public Task<ConsultationResponse> Get(Guid id, CancellationToken cancellationToken) =>
        consultations.GetAsync(id, cancellationToken);

    [HttpPost("api/consultations")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public async Task<ActionResult<ConsultationResponse>> Create(CreateConsultationRequest request, CancellationToken cancellationToken)
    {
        var consultation = await consultations.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = consultation.Id }, consultation);
    }

    [HttpPatch("api/consultations/{id:guid}")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public Task<ConsultationResponse> Update(Guid id, UpdateConsultationRequest request, CancellationToken cancellationToken) =>
        consultations.UpdateAsync(id, request, cancellationToken);

    [HttpPost("api/consultations/{id:guid}/complete")]
    [RequirePermission(Permissions.ConsultationsComplete)]
    public Task<ConsultationResponse> Complete(Guid id, CancellationToken cancellationToken) =>
        consultations.CompleteAsync(id, cancellationToken);

    [HttpPost("api/consultations/{id:guid}/discard")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public Task<ConsultationResponse> Discard(Guid id, DiscardConsultationRequest request, CancellationToken cancellationToken) =>
        consultations.DiscardAsync(id, request, cancellationToken);

    // ---- Diagnoses and treatments (B-12, ADR-012) ----

    [HttpPost("api/consultations/{id:guid}/diagnoses")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public Task<ConsultationResponse> AddDiagnosis(Guid id, CreateDiagnosisRequest request, CancellationToken cancellationToken) =>
        consultations.AddDiagnosisAsync(id, request, cancellationToken);

    /// <summary>After completion only <c>status</c> may change, and only by a veterinarian (checked in the service).</summary>
    [HttpPatch("api/consultations/{id:guid}/diagnoses/{diagnosisId:guid}")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public Task<ConsultationResponse> UpdateDiagnosis(
        Guid id,
        Guid diagnosisId,
        UpdateDiagnosisRequest request,
        CancellationToken cancellationToken) =>
        consultations.UpdateDiagnosisAsync(id, diagnosisId, request, cancellationToken);

    [HttpDelete("api/consultations/{id:guid}/diagnoses/{diagnosisId:guid}")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public Task<ConsultationResponse> RemoveDiagnosis(Guid id, Guid diagnosisId, CancellationToken cancellationToken) =>
        consultations.RemoveDiagnosisAsync(id, diagnosisId, cancellationToken);

    [HttpPost("api/consultations/{id:guid}/treatments")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public Task<ConsultationResponse> AddTreatment(Guid id, CreateTreatmentRequest request, CancellationToken cancellationToken) =>
        consultations.AddTreatmentAsync(id, request, cancellationToken);

    [HttpPatch("api/consultations/{id:guid}/treatments/{treatmentId:guid}")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public Task<ConsultationResponse> UpdateTreatment(
        Guid id,
        Guid treatmentId,
        UpdateTreatmentRequest request,
        CancellationToken cancellationToken) =>
        consultations.UpdateTreatmentAsync(id, treatmentId, request, cancellationToken);

    [HttpDelete("api/consultations/{id:guid}/treatments/{treatmentId:guid}")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public Task<ConsultationResponse> RemoveTreatment(Guid id, Guid treatmentId, CancellationToken cancellationToken) =>
        consultations.RemoveTreatmentAsync(id, treatmentId, cancellationToken);

    /// <summary>The patient's problem list: diagnoses of completed consultations. A clinical read: audited.</summary>
    [HttpGet("api/patients/{patientId:guid}/diagnoses")]
    [RequirePermission(Permissions.ClinicalRead)]
    public Task<PagedResponse<PatientDiagnosisResponse>> ListPatientDiagnoses(
        Guid patientId,
        [FromQuery] PatientDiagnosisListQuery query,
        CancellationToken cancellationToken) =>
        consultations.ListPatientDiagnosesAsync(patientId, query, cancellationToken);

    // ---- Prescriptions (B-13, ADR-013): veterinarians only ----

    [HttpPost("api/consultations/{id:guid}/prescriptions")]
    [RequirePermission(Permissions.PrescriptionsIssue)]
    public Task<ConsultationResponse> AddPrescription(Guid id, CreatePrescriptionRequest request, CancellationToken cancellationToken) =>
        consultations.AddPrescriptionAsync(id, request, cancellationToken);

    [HttpPatch("api/consultations/{id:guid}/prescriptions/{prescriptionId:guid}")]
    [RequirePermission(Permissions.PrescriptionsIssue)]
    public Task<ConsultationResponse> UpdatePrescription(
        Guid id,
        Guid prescriptionId,
        UpdatePrescriptionRequest request,
        CancellationToken cancellationToken) =>
        consultations.UpdatePrescriptionAsync(id, prescriptionId, request, cancellationToken);

    [HttpDelete("api/consultations/{id:guid}/prescriptions/{prescriptionId:guid}")]
    [RequirePermission(Permissions.PrescriptionsIssue)]
    public Task<ConsultationResponse> RemovePrescription(Guid id, Guid prescriptionId, CancellationToken cancellationToken) =>
        consultations.RemovePrescriptionAsync(id, prescriptionId, cancellationToken);

    /// <summary>Only for prescriptions of completed consultations; in progress, edit or remove them instead.</summary>
    [HttpPost("api/consultations/{id:guid}/prescriptions/{prescriptionId:guid}/cancel")]
    [RequirePermission(Permissions.PrescriptionsIssue)]
    public Task<ConsultationResponse> CancelPrescription(
        Guid id,
        Guid prescriptionId,
        CancelPrescriptionRequest request,
        CancellationToken cancellationToken) =>
        consultations.CancelPrescriptionAsync(id, prescriptionId, request, cancellationToken);

    /// <summary>The patient's medication list: prescriptions of completed consultations. A clinical read: audited.</summary>
    [HttpGet("api/patients/{patientId:guid}/prescriptions")]
    [RequirePermission(Permissions.ClinicalRead)]
    public Task<PagedResponse<PatientPrescriptionResponse>> ListPatientPrescriptions(
        Guid patientId,
        [FromQuery] PatientPrescriptionListQuery query,
        CancellationToken cancellationToken) =>
        consultations.ListPatientPrescriptionsAsync(patientId, query, cancellationToken);

    [HttpPost("api/consultations/{id:guid}/addenda")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public Task<ConsultationResponse> AddAddendum(Guid id, AddAddendumRequest request, CancellationToken cancellationToken) =>
        consultations.AddAddendumAsync(id, request, cancellationToken);
}
