using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Features.Appointments;

namespace VetPlatform.Api.Features.Billing;

public sealed record RefundResponse(
    Guid Id,
    decimal Amount,
    PaymentMethod Method,
    string Reason,
    DateTimeOffset RefundedAt,
    AppointmentPersonSummary? RefundedBy)
{
    public static RefundResponse From(Refund r, TimeZoneInfo zone) =>
        new(r.Id, Currencies.Shortest(r.Amount), r.Method, r.Reason, TimeZoneInfo.ConvertTime(r.RefundedAt, zone),
            r.RefundedBy is { } by ? new AppointmentPersonSummary(by.Id, by.Name) : null);
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
    InvoicePaymentSummary Invoice,
    DateTimeOffset CreatedAt)
{
    public static PaymentResponse From(Payment p, Invoice invoice, TimeZoneInfo zone) =>
        new(
            p.Id, Currencies.Shortest(p.Amount), p.Method, p.Reference, TimeZoneInfo.ConvertTime(p.PaidAt, zone),
            p.ReceivedBy is { } by ? new AppointmentPersonSummary(by.Id, by.Name) : null,
            Currencies.Shortest(p.Refunds.Sum(r => r.Amount)),
            p.Refunds.OrderBy(r => r.RefundedAt).ThenBy(r => r.Id).Select(r => RefundResponse.From(r, zone)).ToList(),
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
