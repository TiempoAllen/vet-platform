using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Billing;

/// <summary>
/// Receipts and the per-invoice activity trail (docs/API.md sections 10.5 and 10.6, ADR-018). Receipts are created by
/// <see cref="PaymentService"/> with each payment or refund; this service only reads. Opening a receipt (a view or a
/// reprint) is audited.
/// </summary>
public sealed class ReceiptService(AppDbContext db, ITenantContext tenant, AuditTrail audit)
{
    public async Task<ReceiptResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The receipt was not found.");
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == receipt.InvoiceId, cancellationToken);
        audit.Record(AuditActions.ReceiptViewed, nameof(Receipt), id, new { number = receipt.Number, invoiceId = receipt.InvoiceId });
        await db.SaveChangesAsync(cancellationToken);
        return ReceiptResponse.From(receipt, InvoiceBillTo.From(invoice), zone);
    }

    /// <summary>Newest number first; filters by invoice and exact number. Listing is not audited (opening one is).</summary>
    public async Task<PagedResponse<ReceiptResponse>> ListAsync(ReceiptListQuery query, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var receipts = db.Receipts.AsNoTracking();
        if (query.InvoiceId is { } invoiceId)
        {
            receipts = receipts.Where(r => r.InvoiceId == invoiceId);
        }

        if (query.Number is { } number)
        {
            receipts = receipts.Where(r => r.Number == number);
        }

        var rows = receipts
            .OrderByDescending(r => r.Number)
            .Join(db.Invoices.AsNoTracking(), r => new { r.ClinicId, r.InvoiceId }, i => new { i.ClinicId, InvoiceId = i.Id }, (r, i) => new { Receipt = r, Invoice = i });
        return await rows.ToPageAsync(query, row => ReceiptResponse.From(row.Receipt, InvoiceBillTo.From(row.Invoice), zone), cancellationToken);
    }

    /// <summary>
    /// The audit entries of an invoice, its payments (refunds are recorded on their payment) and its receipts, oldest
    /// first. <c>audit_logs</c> is read only through the clinic-scoped helper (ADR-007).
    /// </summary>
    public async Task<PagedResponse<InvoiceActivityEntry>> ActivityAsync(Guid invoiceId, PageQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Invoices.AnyAsync(i => i.Id == invoiceId, cancellationToken))
        {
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The invoice was not found.");
        }

        var zone = await ZoneAsync(cancellationToken);
        var paymentIds = db.Payments.Where(p => p.InvoiceId == invoiceId).Select(p => (Guid?)p.Id);
        var receiptIds = db.Receipts.Where(r => r.InvoiceId == invoiceId).Select(r => (Guid?)r.Id);
        var entries = db.ForCurrentClinic(tenant).AsNoTracking().Where(e =>
            (e.EntityType == nameof(Invoice) && e.EntityId == invoiceId)
            || (e.EntityType == nameof(Payment) && paymentIds.Contains(e.EntityId))
            || (e.EntityType == nameof(Receipt) && receiptIds.Contains(e.EntityId)));

        var page = await entries.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToPageAsync(query, e => e, cancellationToken);

        // Actors through the clinic-filtered users, so only this clinic's staff are named.
        var actorIds = page.Data.Select(e => e.ActorUserId).OfType<Guid>().Distinct().ToList();
        var actors = actorIds.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);

        var data = page.Data.Select(e => new InvoiceActivityEntry(
            e.Id, TimeZoneInfo.ConvertTime(e.OccurredAt, zone), e.Action, e.EntityType, e.EntityId,
            e.ActorUserId is { } actor && actors.TryGetValue(actor, out var name) ? new AppointmentPersonSummary(actor, name) : null,
            ParseDetails(e.Metadata))).ToList();
        return new PagedResponse<InvoiceActivityEntry>(data, page.Pagination);
    }

    private static JsonElement? ParseDetails(string? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadata);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken)
    {
        var clinicId = tenant.ClinicId ?? throw new InvalidOperationException("Receipts require a current clinic.");
        return ClinicTimeZone.Of(await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == clinicId, cancellationToken));
    }
}
