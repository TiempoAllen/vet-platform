using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Vaccinations;

/// <summary>
/// Vaccinations (docs/API.md section 9, ADR-014). Reads need <c>clinical.read</c> and are audited; recording and voiding
/// need <c>clinical.write</c>; the clinic-wide due list needs <c>vaccinations.read_due</c> (reception included). There
/// is no edit and no delete.
/// </summary>
[ApiController]
public sealed class VaccinationsController(VaccinationService vaccinations) : ControllerBase
{
    [HttpGet("api/patients/{patientId:guid}/vaccinations")]
    [RequirePermission(Permissions.ClinicalRead)]
    public Task<PagedResponse<VaccinationResponse>> ListForPatient(
        Guid patientId,
        [FromQuery] VaccinationListQuery query,
        CancellationToken cancellationToken) =>
        vaccinations.ListForPatientAsync(patientId, query, cancellationToken);

    [HttpPost("api/patients/{patientId:guid}/vaccinations")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public async Task<ActionResult<VaccinationResponse>> Record(
        Guid patientId,
        RecordVaccinationRequest request,
        CancellationToken cancellationToken)
    {
        var vaccination = await vaccinations.RecordAsync(patientId, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = vaccination.Id }, vaccination);
    }

    /// <summary>Declared before <c>{id:guid}</c> for readability; the guid constraint keeps the routes apart anyway.</summary>
    [HttpGet("api/vaccinations/due")]
    [RequirePermission(Permissions.VaccinationsReadDue)]
    public Task<PagedResponse<DueVaccinationResponse>> ListDue([FromQuery] VaccinationDueQuery query, CancellationToken cancellationToken) =>
        vaccinations.ListDueAsync(query, cancellationToken);

    [HttpGet("api/vaccinations/{id:guid}")]
    [RequirePermission(Permissions.ClinicalRead)]
    public Task<VaccinationResponse> Get(Guid id, CancellationToken cancellationToken) =>
        vaccinations.GetAsync(id, cancellationToken);

    [HttpPost("api/vaccinations/{id:guid}/void")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public Task<VaccinationResponse> Void(Guid id, VoidVaccinationRequest request, CancellationToken cancellationToken) =>
        vaccinations.VoidAsync(id, request, cancellationToken);
}
