using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.Billing;

// ---------- Tax rates ----------

public sealed record TaxRateResponse(Guid Id, string Name, decimal RatePercent, TaxRateStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static TaxRateResponse From(TaxRate t) =>
        new(t.Id, t.Name, Currencies.Shortest(t.RatePercent), t.Status, t.CreatedAt, t.UpdatedAt);
}

/// <summary>The percentage is fixed once created: to change a rate, archive it and create a new one.</summary>
public sealed record CreateTaxRateRequest(
    [Required, MaxLength(TaxRate.NameMaxLength)] string? Name,
    [Required] decimal? RatePercent);

/// <summary>Renames, archives (<c>Archived</c>) or restores (<c>Active</c>) a rate.</summary>
public sealed record UpdateTaxRateRequest([MaxLength(TaxRate.NameMaxLength)] string? Name, TaxRateStatus? Status);

public sealed class TaxRateListQuery : PageQuery, IValidatableObject
{
    /// <summary>Active or Archived; without it both are listed.</summary>
    public string? Status { get; init; }

    public TaxRateStatus? ParsedStatus => StrictEnum.TryParse<TaxRateStatus>(Status, out var status) ? status : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<TaxRateStatus>()}.", [nameof(Status)]);
        }
    }
}

// ---------- Invoices ----------

public sealed record InvoiceClientSummary(Guid Id, string FirstName, string LastName);

/// <summary>Who an issued invoice is addressed to, frozen at issue (ADR-018).</summary>
public sealed record InvoiceBillTo(
    string Name,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? Region,
    string? PostalCode,
    string? Country,
    string? PatientName)
{
    public static InvoiceBillTo? From(Invoice i) =>
        i.BillToName is null
            ? null
            : new(i.BillToName, i.BillToAddressLine1, i.BillToAddressLine2, i.BillToCity, i.BillToRegion, i.BillToPostalCode,
                i.BillToCountry, i.BillToPatientName);
}

public sealed record InvoiceTaxRateSummary(Guid Id, string Name, decimal Percent);

public sealed record InvoiceItemResponse(
    Guid Id,
    int Position,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    DiscountType DiscountType,
    decimal DiscountValue,
    InvoiceTaxRateSummary TaxRate,
    Guid? ConsultationId,
    Guid? ProductId,
    decimal Net,
    decimal Discount,
    decimal Tax,
    decimal Total)
{
    public static InvoiceItemResponse From(InvoiceItem i) =>
        new(
            i.Id, i.Position, i.Description, Currencies.Shortest(i.Quantity), Currencies.Shortest(i.UnitPrice), i.DiscountType,
            Currencies.Shortest(i.DiscountValue),
            new InvoiceTaxRateSummary(i.TaxRateId, i.TaxRateName, Currencies.Shortest(i.TaxRatePercent)),
            i.ConsultationId, i.ProductId, Currencies.Shortest(i.Net), Currencies.Shortest(i.Discount), Currencies.Shortest(i.Tax),
            Currencies.Shortest(i.Total));
}

/// <summary>An invoice as returned by the API (docs/API.md section 10). Amounts are decimals in <c>currency</c>.</summary>
public sealed record InvoiceResponse(
    Guid Id,
    int? Number,
    InvoiceStatus Status,
    InvoiceClientSummary? Client,
    AppointmentPatientSummary? Patient,
    InvoiceBillTo? BillTo,
    string Currency,
    string? Notes,
    IReadOnlyList<InvoiceItemResponse> Items,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal Total,
    decimal AmountPaid,
    decimal BalanceDue,
    PaymentStatus? PaymentStatus,
    DateTimeOffset? IssuedAt,
    DateOnly? DueOn,
    AppointmentPersonSummary? IssuedBy,
    DateTimeOffset? VoidedAt,
    AppointmentPersonSummary? VoidedBy,
    string? VoidReason,
    AppointmentPersonSummary? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Client, patient, items and the people must be loaded (a missing one shows null).</summary>
    public static InvoiceResponse From(Invoice i, TimeZoneInfo zone) =>
        new(
            i.Id, i.Number, i.Status,
            i.Client is { } c ? new InvoiceClientSummary(c.Id, c.FirstName, c.LastName) : null,
            i.Patient is { } p ? new AppointmentPatientSummary(p.Id, p.Name, p.Species) : null,
            InvoiceBillTo.From(i),
            i.Currency, i.Notes,
            i.Items.OrderBy(item => item.Position).Select(InvoiceItemResponse.From).ToList(),
            Currencies.Shortest(i.Subtotal), Currencies.Shortest(i.DiscountTotal), Currencies.Shortest(i.TaxTotal),
            Currencies.Shortest(i.Total), Currencies.Shortest(i.AmountPaid), Currencies.Shortest(i.BalanceDue), i.PaymentStatus,
            Local(i.IssuedAt, zone), i.DueOn, Person(i.IssuedBy), Local(i.VoidedAt, zone), Person(i.VoidedBy), i.VoidReason,
            Person(i.CreatedBy), i.CreatedAt, i.UpdatedAt);

    private static AppointmentPersonSummary? Person(Identity.User? user) =>
        user is null ? null : new AppointmentPersonSummary(user.Id, user.Name);

    private static DateTimeOffset? Local(DateTimeOffset? instant, TimeZoneInfo zone) =>
        instant is { } value ? TimeZoneInfo.ConvertTime(value, zone) : null;
}

/// <summary>A new draft. <c>patientId</c> (optional) must be a patient currently owned by the client.</summary>
public sealed record CreateInvoiceRequest(
    [Required] Guid? ClientId,
    Guid? PatientId,
    [MaxLength(Invoice.NotesMaxLength)] string? Notes);

/// <summary>Edits a draft. Omitted fields are unchanged; <c>patientId: null</c> removes the patient; <c>""</c> clears notes.</summary>
public sealed record UpdateInvoiceRequest(
    Guid? ClientId,
    Optional<Guid?> PatientId,
    [MaxLength(Invoice.NotesMaxLength)] string? Notes);

/// <summary>
/// A new line. <c>discountType</c> defaults to <c>None</c> (then <c>discountValue</c> must be omitted or 0).
/// <c>consultationId</c> (optional) must be a non-discarded consultation of the invoice's patient. <c>productId</c>
/// (optional) must be an active product of the clinic; the description and price are still given here (B-19).
/// </summary>
public sealed record CreateInvoiceItemRequest(
    [Required, MaxLength(InvoiceItem.DescriptionMaxLength)] string? Description,
    [Required] decimal? Quantity,
    [Required] decimal? UnitPrice,
    DiscountType? DiscountType,
    decimal? DiscountValue,
    [Required] Guid? TaxRateId,
    Guid? ConsultationId,
    Guid? ProductId);

/// <summary>Partial update of a draft's line; <c>consultationId: null</c> or <c>productId: null</c> unlinks it.</summary>
public sealed record UpdateInvoiceItemRequest(
    [MaxLength(InvoiceItem.DescriptionMaxLength)] string? Description,
    decimal? Quantity,
    decimal? UnitPrice,
    DiscountType? DiscountType,
    decimal? DiscountValue,
    Guid? TaxRateId,
    Optional<Guid?> ConsultationId,
    Optional<Guid?> ProductId);

/// <summary><c>dueOn</c> defaults to the clinic-local issue day and may not be earlier.</summary>
public sealed record IssueInvoiceRequest(DateOnly? DueOn);

public sealed record VoidInvoiceRequest([Required, MaxLength(Invoice.ReasonMaxLength)] string? Reason);

public sealed class InvoiceListQuery : PageQuery, IValidatableObject
{
    public string? Status { get; init; }

    public Guid? ClientId { get; init; }

    public Guid? PatientId { get; init; }

    /// <summary>Unpaid, PartiallyPaid or Paid; only issued invoices have one.</summary>
    public string? PaymentStatus { get; init; }

    /// <summary>true: issued invoices with a balance left whose due date (clinic-local) has passed.</summary>
    public bool? Overdue { get; init; }

    public InvoiceStatus? ParsedStatus => StrictEnum.TryParse<InvoiceStatus>(Status, out var status) ? status : null;

    public PaymentStatus? ParsedPaymentStatus =>
        StrictEnum.TryParse<PaymentStatus>(PaymentStatus, out var status) ? status : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<InvoiceStatus>()}.", [nameof(Status)]);
        }

        if (PaymentStatus is not null && ParsedPaymentStatus is null)
        {
            yield return new ValidationResult(
                $"PaymentStatus must be one of {StrictEnum.Names<PaymentStatus>()}.", [nameof(PaymentStatus)]);
        }
    }
}
