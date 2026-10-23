using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Patients;

/// <summary>Patients of the caller's clinic (docs/API.md section 6). There is no delete: deactivate or mark deceased.</summary>
[ApiController]
[Route("api/patients")]
public sealed class PatientsController(PatientService patients, PatientHistoryService history) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.PatientsRead)]
    public Task<PagedResponse<PatientResponse>> List([FromQuery] PatientListQuery query, CancellationToken cancellationToken) =>
        patients.ListAsync(query, cancellationToken);

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.PatientsRead)]
    public Task<PatientResponse> Get(Guid id, CancellationToken cancellationToken) =>
        patients.GetAsync(id, cancellationToken);

    /// <summary>The patient's timeline, newest first (docs/API.md section 6.1).</summary>
    [HttpGet("{id:guid}/history")]
    [RequirePermission(Permissions.PatientsRead)]
    public Task<PagedResponse<PatientHistoryEntry>> History(
        Guid id,
        [FromQuery] PageQuery query,
        CancellationToken cancellationToken) =>
        history.ListAsync(
            id, query, Permissions.Grant(Permissions.RolesOf(User), Permissions.ClinicalRead), cancellationToken);

    [HttpPost]
    [RequirePermission(Permissions.PatientsManage)]
    public async Task<ActionResult<PatientResponse>> Create(CreatePatientRequest request, CancellationToken cancellationToken)
    {
        var patient = await patients.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = patient.Id }, patient);
    }

    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.PatientsManage)]
    public Task<PatientResponse> Update(Guid id, UpdatePatientRequest request, CancellationToken cancellationToken) =>
        patients.UpdateAsync(id, request, cancellationToken);
}
