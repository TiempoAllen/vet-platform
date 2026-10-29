using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Billing;

/// <summary>
/// Invoices (docs/API.md section 10, ADR-016): <c>billing.read</c> reads (Admin, Veterinarian, Receptionist);
/// <c>billing.manage</c> writes, issues and voids (Admin, Receptionist).
/// </summary>
[ApiController]
public sealed class InvoicesController(InvoiceService invoices) : ControllerBase
{
    [HttpGet("api/invoices")]
    [RequirePermission(Permissions.BillingRead)]
    public Task<PagedResponse<InvoiceResponse>> List([FromQuery] InvoiceListQuery query, CancellationToken cancellationToken) =>
        invoices.ListAsync(query, cancellationToken);

    [HttpGet("api/invoices/{id:guid}")]
    [RequirePermission(Permissions.BillingRead)]
    public Task<InvoiceResponse> Get(Guid id, CancellationToken cancellationToken) => invoices.GetAsync(id, cancellationToken);

    [HttpPost("api/invoices")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<ActionResult<InvoiceResponse>> Create(CreateInvoiceRequest request, CancellationToken cancellationToken)
    {
        var invoice = await invoices.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = invoice.Id }, invoice);
    }

    [HttpPatch("api/invoices/{id:guid}")]
    [RequirePermission(Permissions.BillingManage)]
    public Task<InvoiceResponse> Update(Guid id, UpdateInvoiceRequest request, CancellationToken cancellationToken) =>
        invoices.UpdateAsync(id, request, cancellationToken);

    /// <summary>Drafts only; an issued invoice is voided instead.</summary>
    [HttpDelete("api/invoices/{id:guid}")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await invoices.DeleteDraftAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("api/invoices/{id:guid}/items")]
    [RequirePermission(Permissions.BillingManage)]
    public Task<InvoiceResponse> AddItem(Guid id, CreateInvoiceItemRequest request, CancellationToken cancellationToken) =>
        invoices.AddItemAsync(id, request, cancellationToken);

    [HttpPatch("api/invoices/{id:guid}/items/{itemId:guid}")]
    [RequirePermission(Permissions.BillingManage)]
    public Task<InvoiceResponse> UpdateItem(Guid id, Guid itemId, UpdateInvoiceItemRequest request, CancellationToken cancellationToken) =>
        invoices.UpdateItemAsync(id, itemId, request, cancellationToken);

    [HttpDelete("api/invoices/{id:guid}/items/{itemId:guid}")]
    [RequirePermission(Permissions.BillingManage)]
    public Task<InvoiceResponse> RemoveItem(Guid id, Guid itemId, CancellationToken cancellationToken) =>
        invoices.RemoveItemAsync(id, itemId, cancellationToken);

    /// <summary>The body is optional (<c>{ "dueOn": "YYYY-MM-DD" }</c>).</summary>
    [HttpPost("api/invoices/{id:guid}/issue")]
    [RequirePermission(Permissions.BillingManage)]
    public Task<InvoiceResponse> Issue(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] IssueInvoiceRequest? request,
        CancellationToken cancellationToken) =>
        invoices.IssueAsync(id, request, cancellationToken);

    [HttpPost("api/invoices/{id:guid}/void")]
    [RequirePermission(Permissions.BillingManage)]
    public Task<InvoiceResponse> Void(Guid id, VoidInvoiceRequest request, CancellationToken cancellationToken) =>
        invoices.VoidAsync(id, request, cancellationToken);
}

/// <summary>
/// Payments and refunds (B-17, ADR-017): read with <c>billing.read</c>; recorded with <c>payments.record</c> (Admin,
/// Receptionist); refunded with <c>payments.refund</c> (Admin). Nothing is edited or deleted.
/// </summary>
[ApiController]
public sealed class PaymentsController(PaymentService payments) : ControllerBase
{
    [HttpGet("api/invoices/{id:guid}/payments")]
    [RequirePermission(Permissions.BillingRead)]
    public Task<IReadOnlyList<PaymentResponse>> List(Guid id, CancellationToken cancellationToken) =>
        payments.ListAsync(id, cancellationToken);

    /// <summary>No single-payment GET exists (the list is short and part of the invoice screen), so no Location header.</summary>
    [HttpPost("api/invoices/{id:guid}/payments")]
    [RequirePermission(Permissions.PaymentsRecord)]
    public async Task<ActionResult<PaymentResponse>> Record(Guid id, RecordPaymentRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await payments.RecordAsync(id, request, cancellationToken));

    /// <summary>Returns the payment with all its refunds (201: a refund was created).</summary>
    [HttpPost("api/invoices/{id:guid}/payments/{paymentId:guid}/refunds")]
    [RequirePermission(Permissions.PaymentsRefund)]
    public async Task<ActionResult<PaymentResponse>> Refund(
        Guid id,
        Guid paymentId,
        RefundPaymentRequest request,
        CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await payments.RefundAsync(id, paymentId, request, cancellationToken));
}

/// <summary>
/// Receipts and the per-invoice activity trail (B-18, ADR-018), read with <c>billing.read</c>. Receipts are created with
/// each payment and refund; opening one is audited.
/// </summary>
[ApiController]
public sealed class ReceiptsController(ReceiptService receipts) : ControllerBase
{
    [HttpGet("api/receipts")]
    [RequirePermission(Permissions.BillingRead)]
    public Task<PagedResponse<ReceiptResponse>> List([FromQuery] ReceiptListQuery query, CancellationToken cancellationToken) =>
        receipts.ListAsync(query, cancellationToken);

    [HttpGet("api/receipts/{id:guid}")]
    [RequirePermission(Permissions.BillingRead)]
    public Task<ReceiptResponse> Get(Guid id, CancellationToken cancellationToken) => receipts.GetAsync(id, cancellationToken);

    [HttpGet("api/invoices/{id:guid}/activity")]
    [RequirePermission(Permissions.BillingRead)]
    public Task<PagedResponse<InvoiceActivityEntry>> Activity(Guid id, [FromQuery] PageQuery query, CancellationToken cancellationToken) =>
        receipts.ActivityAsync(id, query, cancellationToken);
}

/// <summary>Tax rates: readable by billing staff and administrators; managed with <c>settings.manage</c> (Admin).</summary>
[ApiController]
public sealed class TaxRatesController(TaxRateService taxRates) : ControllerBase
{
    [HttpGet("api/tax-rates")]
    [RequireAnyPermission(Permissions.BillingRead, Permissions.SettingsManage)]
    public Task<PagedResponse<TaxRateResponse>> List([FromQuery] TaxRateListQuery query, CancellationToken cancellationToken) =>
        taxRates.ListAsync(query, cancellationToken);

    [HttpPost("api/tax-rates")]
    [RequirePermission(Permissions.SettingsManage)]
    public async Task<ActionResult<TaxRateResponse>> Create(CreateTaxRateRequest request, CancellationToken cancellationToken)
    {
        // No single-rate GET exists (the list is short), so there is no Location header.
        return StatusCode(StatusCodes.Status201Created, await taxRates.CreateAsync(request, cancellationToken));
    }

    [HttpPatch("api/tax-rates/{id:guid}")]
    [RequirePermission(Permissions.SettingsManage)]
    public Task<TaxRateResponse> Update(Guid id, UpdateTaxRateRequest request, CancellationToken cancellationToken) =>
        taxRates.UpdateAsync(id, request, cancellationToken);
}
