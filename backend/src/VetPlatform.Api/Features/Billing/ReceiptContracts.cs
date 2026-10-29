using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Features.Appointments;

namespace VetPlatform.Api.Features.Billing;

public sealed record ReceiptInvoice(Guid Id, int Number, decimal Total, string Currency);

/// <summary>
/// A receipt as returned by the API (docs/API.md section 10.5): everything printed on it, frozen when it was created.
/// <c>billTo</c> is the invoice's frozen bill-to block. Instants carry the clinic's offset.
/// </summary>
public sealed record ReceiptResponse(
    Guid Id,
    int Number,
    ReceiptKind Kind,
    string ClinicName,
    ReceiptInvoice Invoice,
    InvoiceBillTo? BillTo,
    Guid PaymentId,
    Guid? RefundId,
    decimal Amount,
    PaymentMethod Method,
    string? Reference,
    DateTimeOffset OccurredAt,
    decimal AmountPaidAfter,
    decimal BalanceDueAfter,
    AppointmentPersonSummary IssuedBy,
    DateTimeOffset CreatedAt)
{
    public static ReceiptResponse From(Receipt r, InvoiceBillTo? billTo, TimeZoneInfo zone) =>
        new(
            r.Id, r.Number, r.Kind, r.ClinicName,
            new ReceiptInvoice(r.InvoiceId, r.InvoiceNumber, Currencies.Shortest(r.InvoiceTotal), r.Currency),
            billTo, r.PaymentId, r.RefundId, Currencies.Shortest(r.Amount), r.Method, r.Reference,
            TimeZoneInfo.ConvertTime(r.OccurredAt, zone), Currencies.Shortest(r.AmountPaidAfter),
            Currencies.Shortest(r.BalanceDueAfter), new AppointmentPersonSummary(r.IssuedByUserId, r.IssuedByName), r.CreatedAt);
}

public sealed class ReceiptListQuery : PageQuery, IValidatableObject
{
    public Guid? InvoiceId { get; init; }

    /// <summary>An exact receipt number.</summary>
    public int? Number { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Number is <= 0)
        {
            yield return new ValidationResult("Number must be a positive receipt number.", [nameof(Number)]);
        }
    }
}

/// <summary>
/// One audit entry of an invoice, its payments or its receipts (docs/API.md section 10.6). <c>details</c> is the entry's
/// metadata, which never holds free text (descriptions, notes, references, reasons).
/// </summary>
public sealed record InvoiceActivityEntry(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Action,
    string? EntityType,
    Guid? EntityId,
    AppointmentPersonSummary? Actor,
    JsonElement? Details);
