using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Appointments;

/// <summary>
/// Appointments of the caller's clinic (docs/API.md section 7). B-09: book, edit, reschedule. Status changes
/// (check-in, complete, cancel, no-show) arrive in B-10. There is no delete.
/// </summary>
[ApiController]
[Route("api/appointments")]
public sealed class AppointmentsController(AppointmentService appointments) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.AppointmentsRead)]
    public Task<PagedResponse<AppointmentResponse>> List([FromQuery] AppointmentListQuery query, CancellationToken cancellationToken) =>
        appointments.ListAsync(query, cancellationToken);

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.AppointmentsRead)]
    public Task<AppointmentResponse> Get(Guid id, CancellationToken cancellationToken) =>
        appointments.GetAsync(id, cancellationToken);

    [HttpPost]
    [RequirePermission(Permissions.AppointmentsManage)]
    public async Task<ActionResult<AppointmentResponse>> Create(CreateAppointmentRequest request, CancellationToken cancellationToken)
    {
        var appointment = await appointments.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = appointment.Id }, appointment);
    }

    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.AppointmentsManage)]
    public Task<AppointmentResponse> Update(Guid id, UpdateAppointmentRequest request, CancellationToken cancellationToken) =>
        appointments.UpdateAsync(id, request, cancellationToken);
}
