using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Appointments;

/// <summary>
/// Appointments of the caller's clinic (docs/API.md section 7): book, edit and reschedule (B-09), and the status
/// lifecycle (B-10). Each status change is its own action, so rules and permissions stay explicit. There is no delete.
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

    [HttpPost("{id:guid}/check-in")]
    [RequirePermission(Permissions.AppointmentsManage)]
    public Task<AppointmentResponse> CheckIn(Guid id, CancellationToken cancellationToken) =>
        appointments.CheckInAsync(id, cancellationToken);

    [HttpPost("{id:guid}/undo-check-in")]
    [RequirePermission(Permissions.AppointmentsManage)]
    public Task<AppointmentResponse> UndoCheckIn(Guid id, CancellationToken cancellationToken) =>
        appointments.UndoCheckInAsync(id, cancellationToken);

    [HttpPost("{id:guid}/complete")]
    [RequirePermission(Permissions.AppointmentsManage)]
    public Task<AppointmentResponse> Complete(Guid id, CancellationToken cancellationToken) =>
        appointments.CompleteAsync(id, cancellationToken);

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.AppointmentsManage)]
    public Task<AppointmentResponse> Cancel(Guid id, CancelAppointmentRequest request, CancellationToken cancellationToken) =>
        appointments.CancelAsync(id, request, cancellationToken);

    [HttpPost("{id:guid}/no-show")]
    [RequirePermission(Permissions.AppointmentsManage)]
    public Task<AppointmentResponse> NoShow(Guid id, CancellationToken cancellationToken) =>
        appointments.NoShowAsync(id, cancellationToken);

    [HttpPost("{id:guid}/undo-no-show")]
    [RequirePermission(Permissions.AppointmentsManage)]
    public Task<AppointmentResponse> UndoNoShow(Guid id, CancellationToken cancellationToken) =>
        appointments.UndoNoShowAsync(id, cancellationToken);
}
