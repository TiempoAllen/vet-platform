using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Patients;

/// <summary>Patients of the caller's clinic (docs/API.md section 6). There is no delete: deactivate or mark deceased.</summary>
[ApiController]
[Route("api/patients")]
public sealed class PatientsController(PatientService patients) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.PatientsRead)]
    public Task<PagedResponse<PatientResponse>> List([FromQuery] PatientListQuery query, CancellationToken cancellationToken) =>
        patients.ListAsync(query, cancellationToken);

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.PatientsRead)]
    public Task<PatientResponse> Get(Guid id, CancellationToken cancellationToken) =>
        patients.GetAsync(id, cancellationToken);

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
