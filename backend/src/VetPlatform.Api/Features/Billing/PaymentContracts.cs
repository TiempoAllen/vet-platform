using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Features.Appointments;

namespace VetPlatform.Api.Features.Billing;

/// <summary>The receipt of a payment or refund; null only for money recorded before receipts existed (B-18).</summary>
public sealed record ReceiptSummary(Guid Id, int Number);

/// <summary>The receipts of one invoice, looked up by payment (payment receipts) and by refund.</summary>
public sealed class InvoiceReceipts(IEnumerable<Receipt> receipts)
{
    private readonly Dictionary<Guid, ReceiptSummary> _byPayment = receipts.Where(r => r.Kind == ReceiptKind.Payment)
        .ToDictionary(r => r.PaymentId, r => new ReceiptSummary(r.Id, r.Number));

    private readonly Dictionary<Guid, ReceiptSummary> _byRefund = receipts.Where(r => r.RefundId is not null)
        .ToDictionary(r => r.RefundId!.Value, r => new ReceiptSummary(r.Id, r.Number));

    public ReceiptSummary? ForPayment(Guid paymentId) => _byPayment.GetValueOrDefault(paymentId);

    public ReceiptSummary? ForRefund(Guid refundId) => _byRefund.GetValueOrDefault(refundId);
}

public sealed record RefundResponse(
    Guid Id,
    decimal Amount,
    PaymentMethod Method,
    string Reason,
    DateTimeOffset RefundedAt,
    AppointmentPersonSummary? RefundedBy,
    ReceiptSummary? Receipt)
{
    public static RefundResponse From(Refund r, TimeZoneInfo zone, InvoiceReceipts receipts) =>
        new(r.Id, Currencies.Shortest(r.Amount), r.Method, r.Reason, TimeZoneInfo.ConvertTime(r.RefundedAt, zone),
            r.RefundedBy is { } by ? new AppointmentPersonSummary(by.Id, by.Name) : null, receipts.ForRefund(r.Id));
}

/// <summary>The invoice's payment state after a payment or refund, so a client need not reload the invoice.</summary>
public sealed record InvoicePaymentSummary(Guid Id, decimal Total, decimal AmountPaid, decimal BalanceDue, PaymentStatus? PaymentStatus)
{
    public static InvoicePaymentSummary From(Invoice i) =>
        new(i.Id, Currencies.Shortest(i.Total), Currencies.Shortest(i.AmountPaid), Currencies.Shortest(i.BalanceDue), i.PaymentStatus);
}

/// <summary>A payment with its refunds (docs/API.md section 10.4). Instants carry the clinic's offset.</summary>
public sealed record PaymentResponse(
    Guid Id,
    decimal Amount,
    PaymentMethod Method,
    string? Reference,
    DateTimeOffset PaidAt,
    AppointmentPersonSummary? ReceivedBy,
    decimal RefundedAmount,
    IReadOnlyList<RefundResponse> Refunds,
    ReceiptSummary? Receipt,
    InvoicePaymentSummary Invoice,
    DateTimeOffset CreatedAt)
{
    public static PaymentResponse From(Payment p, Invoice invoice, TimeZoneInfo zone, InvoiceReceipts receipts) =>
        new(
            p.Id, Currencies.Shortest(p.Amount), p.Method, p.Reference, TimeZoneInfo.ConvertTime(p.PaidAt, zone),
            p.ReceivedBy is { } by ? new AppointmentPersonSummary(by.Id, by.Name) : null,
            Currencies.Shortest(p.Refunds.Sum(r => r.Amount)),
            p.Refunds.OrderBy(r => r.RefundedAt).ThenBy(r => r.Id).Select(r => RefundResponse.From(r, zone, receipts)).ToList(),
            receipts.ForPayment(p.Id),
            InvoicePaymentSummary.From(invoice),
            p.CreatedAt);
}

/// <summary>
/// <c>paidAt</c> (optional, with an explicit offset) defaults to now; it may be earlier but not before the invoice was
/// issued, and never in the future.
/// </summary>
public sealed record RecordPaymentRequest(
    [Required] decimal? Amount,
    [Required] PaymentMethod? Method,
    [MaxLength(Payment.ReferenceMaxLength)] string? Reference,
    DateTimeOffset? PaidAt);

public sealed record RefundPaymentRequest(
    [Required] decimal? Amount,
    [Required] PaymentMethod? Method,
    [Required, MaxLength(Refund.ReasonMaxLength)] string? Reason);
