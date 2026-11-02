using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;

namespace VetPlatform.Api.Features.Reports;

/// <summary>
/// Reports (docs/API.md section 12): operational (B-23, ADR-023) and financial (B-24, ADR-024) ones with
/// <c>reports.read</c> (Admin), inventory ones with <c>reports.inventory</c> (Admin, Inventory).
/// </summary>
[ApiController]
[Route("api/reports")]
public sealed class ReportsController(ReportService reports, FinancialReportService financial, InventoryReportService inventory) : ControllerBase
{
    [HttpGet("appointments")]
    [RequirePermission(Permissions.ReportsRead)]
    public Task<AppointmentsReportResponse> Appointments([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.AppointmentsAsync(query, cancellationToken);

    [HttpGet("patients-seen")]
    [RequirePermission(Permissions.ReportsRead)]
    public Task<PatientsSeenReportResponse> PatientsSeen([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.PatientsSeenAsync(query, cancellationToken);

    [HttpGet("revenue")]
    [RequirePermission(Permissions.ReportsRead)]
    public Task<RevenueReportResponse> Revenue([FromQuery] ReportPeriodQuery query, CancellationToken cancellationToken) =>
        financial.RevenueAsync(query, cancellationToken);

    [HttpGet("payments")]
    [RequirePermission(Permissions.ReportsRead)]
    public Task<PaymentsReportResponse> Payments([FromQuery] ReportPeriodQuery query, CancellationToken cancellationToken) =>
        financial.PaymentsAsync(query, cancellationToken);

    [HttpGet("receivables")]
    [RequirePermission(Permissions.ReportsRead)]
    public Task<ReceivablesReportResponse> Receivables(CancellationToken cancellationToken) =>
        financial.ReceivablesAsync(cancellationToken);

    [HttpGet("stock-valuation")]
    [RequirePermission(Permissions.ReportsInventory)]
    public Task<StockValuationReportResponse> StockValuation(CancellationToken cancellationToken) =>
        inventory.ValuationAsync(cancellationToken);

    [HttpGet("stock-movements")]
    [RequirePermission(Permissions.ReportsInventory)]
    public Task<StockMovementsReportResponse> StockMovements([FromQuery] ReportDateRange query, CancellationToken cancellationToken) =>
        inventory.MovementsAsync(query, cancellationToken);
}

/// <summary>A client's statement (docs/API.md section 5.1, ADR-024), read with <c>billing.read</c>.</summary>
[ApiController]
public sealed class ClientStatementController(FinancialReportService financial) : ControllerBase
{
    [HttpGet("api/clients/{id:guid}/statement")]
    [RequirePermission(Permissions.BillingRead)]
    public Task<ClientStatementResponse> Statement(Guid id, [FromQuery] ReportDateRange query, CancellationToken cancellationToken) =>
        financial.StatementAsync(id, query, cancellationToken);
}
