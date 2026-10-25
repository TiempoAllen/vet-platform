using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.ClinicalNotes;

/// <summary>Clinical notes: reads need <c>clinical.read</c> and are audited; adding needs <c>clinical.write</c>.</summary>
[ApiController]
public sealed class ClinicalNotesController(ClinicalNoteService notes) : ControllerBase
{
    [HttpGet("api/patients/{patientId:guid}/clinical-notes")]
    [RequirePermission(Permissions.ClinicalRead)]
    public Task<PagedResponse<ClinicalNoteResponse>> ListForPatient(Guid patientId, [FromQuery] PageQuery query, CancellationToken cancellationToken) =>
        notes.ListForPatientAsync(patientId, query, cancellationToken);

    [HttpPost("api/patients/{patientId:guid}/clinical-notes")]
    [RequirePermission(Permissions.ClinicalWrite)]
    public async Task<ActionResult<ClinicalNoteResponse>> Add(Guid patientId, AddClinicalNoteRequest request, CancellationToken cancellationToken)
    {
        var note = await notes.AddAsync(patientId, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = note.Id }, note);
    }

    [HttpGet("api/clinical-notes/{id:guid}")]
    [RequirePermission(Permissions.ClinicalRead)]
    public Task<ClinicalNoteResponse> Get(Guid id, CancellationToken cancellationToken) =>
        notes.GetAsync(id, cancellationToken);
}
