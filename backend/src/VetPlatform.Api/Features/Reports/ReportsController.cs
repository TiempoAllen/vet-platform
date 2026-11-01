using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;

namespace VetPlatform.Api.Features.Reports;

/// <summary>Operational reports (docs/API.md section 12, ADR-023), read with <c>reports.read</c> (Admin).</summary>
[ApiController]
[Route("api/reports")]
public sealed class ReportsController(ReportService reports) : ControllerBase
{
    [HttpGet("appointments")]
    [RequirePermission(Permissions.ReportsRead)]
    public Task<AppointmentsReportResponse> Appointments([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.AppointmentsAsync(query, cancellationToken);

    [HttpGet("patients-seen")]
    [RequirePermission(Permissions.ReportsRead)]
    public Task<PatientsSeenReportResponse> PatientsSeen([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.PatientsSeenAsync(query, cancellationToken);
}
