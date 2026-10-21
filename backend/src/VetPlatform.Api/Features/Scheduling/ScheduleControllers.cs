using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Scheduling;

/// <summary>
/// Veterinarians, their weekly hours and computed working periods (docs/API.md section 7.1). Writes are further
/// checked in <see cref="ScheduleService"/>: <c>schedules.manage</c>, or <c>schedules.manage_own</c> for one's own.
/// </summary>
[ApiController]
[Route("api/veterinarians")]
public sealed class VeterinariansController(ScheduleService schedules) : ControllerBase
{
    /// <summary>Active veterinarians of the clinic, by name.</summary>
    [HttpGet]
    [RequirePermission(Permissions.SchedulesRead)]
    public Task<PagedResponse<VeterinarianResponse>> List([FromQuery] PageQuery query, CancellationToken cancellationToken) =>
        schedules.ListVeterinariansAsync(query, cancellationToken);

    [HttpGet("{veterinarianId:guid}/weekly-hours")]
    [RequirePermission(Permissions.SchedulesRead)]
    public Task<WorkingHoursBlockList> ListBlocks(Guid veterinarianId, CancellationToken cancellationToken) =>
        schedules.ListBlocksAsync(veterinarianId, cancellationToken);

    [HttpPost("{veterinarianId:guid}/weekly-hours")]
    [RequireAnyPermission(Permissions.SchedulesManage, Permissions.SchedulesManageOwn)]
    public async Task<ActionResult<WorkingHoursBlockResponse>> CreateBlock(
        Guid veterinarianId,
        CreateWorkingHoursBlockRequest request,
        CancellationToken cancellationToken)
    {
        var block = await schedules.CreateBlockAsync(User, veterinarianId, request, cancellationToken);
        return Created($"/api/veterinarians/{veterinarianId}/weekly-hours/{block.Id}", block);
    }

    [HttpPatch("{veterinarianId:guid}/weekly-hours/{blockId:guid}")]
    [RequireAnyPermission(Permissions.SchedulesManage, Permissions.SchedulesManageOwn)]
    public Task<WorkingHoursBlockResponse> UpdateBlock(
        Guid veterinarianId,
        Guid blockId,
        UpdateWorkingHoursBlockRequest request,
        CancellationToken cancellationToken) =>
        schedules.UpdateBlockAsync(User, veterinarianId, blockId, request, cancellationToken);

    [HttpDelete("{veterinarianId:guid}/weekly-hours/{blockId:guid}")]
    [RequireAnyPermission(Permissions.SchedulesManage, Permissions.SchedulesManageOwn)]
    public async Task<IActionResult> DeleteBlock(Guid veterinarianId, Guid blockId, CancellationToken cancellationToken)
    {
        await schedules.DeleteBlockAsync(User, veterinarianId, blockId, cancellationToken);
        return NoContent();
    }

    /// <summary>Actual working periods: weekly hours minus exceptions and clinic closures, in the clinic's time zone.</summary>
    [HttpGet("{veterinarianId:guid}/working-hours")]
    [RequirePermission(Permissions.SchedulesRead)]
    public Task<WorkingHoursResponse> WorkingHours(
        Guid veterinarianId,
        [FromQuery] WorkingHoursQuery query,
        CancellationToken cancellationToken) =>
        schedules.WorkingHoursAsync(veterinarianId, query, cancellationToken);
}

/// <summary>Veterinarians' exceptions (leave, sickness, ...) and clinic-wide closures (docs/API.md section 7.1).</summary>
[ApiController]
[Route("api/schedule-exceptions")]
public sealed class ScheduleExceptionsController(ScheduleService schedules) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.SchedulesRead)]
    public Task<PagedResponse<ScheduleExceptionResponse>> List(
        [FromQuery] ScheduleExceptionListQuery query,
        CancellationToken cancellationToken) =>
        schedules.ListExceptionsAsync(User, query, cancellationToken);

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.SchedulesRead)]
    public Task<ScheduleExceptionResponse> Get(Guid id, CancellationToken cancellationToken) =>
        schedules.GetExceptionAsync(User, id, cancellationToken);

    [HttpPost]
    [RequireAnyPermission(Permissions.SchedulesManage, Permissions.SchedulesManageOwn)]
    public async Task<ActionResult<ScheduleExceptionResponse>> Create(
        CreateScheduleExceptionRequest request,
        CancellationToken cancellationToken)
    {
        var exception = await schedules.CreateExceptionAsync(User, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = exception.Id }, exception);
    }

    [HttpPatch("{id:guid}")]
    [RequireAnyPermission(Permissions.SchedulesManage, Permissions.SchedulesManageOwn)]
    public Task<ScheduleExceptionResponse> Update(
        Guid id,
        UpdateScheduleExceptionRequest request,
        CancellationToken cancellationToken) =>
        schedules.UpdateExceptionAsync(User, id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    [RequireAnyPermission(Permissions.SchedulesManage, Permissions.SchedulesManageOwn)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await schedules.DeleteExceptionAsync(User, id, cancellationToken);
        return NoContent();
    }
}
