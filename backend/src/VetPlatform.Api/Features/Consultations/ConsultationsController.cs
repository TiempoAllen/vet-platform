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

    [HttpPost("api/consultations/{id:guid}/addenda")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public Task<ConsultationResponse> AddAddendum(Guid id, AddAddendumRequest request, CancellationToken cancellationToken) =>
        consultations.AddAddendumAsync(id, request, cancellationToken);
}
