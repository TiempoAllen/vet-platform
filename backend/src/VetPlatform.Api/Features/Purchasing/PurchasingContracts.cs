using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Clients;
using VetPlatform.Api.Features.Inventory;

namespace VetPlatform.Api.Features.Purchasing;

// ---------- Suppliers ----------

public sealed record SupplierResponse(
    Guid Id,
    string Name,
    string? ContactName,
    string? Email,
    string? Phone,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? Region,
    string? PostalCode,
    string? Country,
    string? AccountNumber,
    string? Notes,
    SupplierStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static SupplierResponse From(Supplier s) =>
        new(
            s.Id, s.Name, s.ContactName, s.Email, s.Phone, s.AddressLine1, s.AddressLine2, s.City, s.Region, s.PostalCode,
            s.Country, s.AccountNumber, s.Notes, s.Status, s.CreatedAt, s.UpdatedAt);
}

/// <summary>A new supplier. Optional text may be omitted, null or blank.</summary>
public sealed record CreateSupplierRequest(
    [Required, MaxLength(Supplier.NameMaxLength)] string? Name,
    [MaxLength(Supplier.NameMaxLength)] string? ContactName,
    [MaxLength(Client.EmailMaxLength)] string? Email,
    [MaxLength(Client.PhoneMaxLength)] string? Phone,
    [MaxLength(Client.AddressLineMaxLength)] string? AddressLine1,
    [MaxLength(Client.AddressLineMaxLength)] string? AddressLine2,
    [MaxLength(Client.LocalityMaxLength)] string? City,
    [MaxLength(Client.LocalityMaxLength)] string? Region,
    [MaxLength(Client.PostalCodeMaxLength)] string? PostalCode,
    [MaxLength(Client.LocalityMaxLength)] string? Country,
    [MaxLength(Supplier.AccountNumberMaxLength)] string? AccountNumber,
    [MaxLength(Supplier.NotesMaxLength)] string? Notes);

/// <summary>Partial update: omitted or null fields are unchanged; <c>""</c> clears optional text.</summary>
public sealed record UpdateSupplierRequest(
    [MaxLength(Supplier.NameMaxLength)] string? Name,
    [MaxLength(Supplier.NameMaxLength)] string? ContactName,
    [MaxLength(Client.EmailMaxLength)] string? Email,
    [MaxLength(Client.PhoneMaxLength)] string? Phone,
    [MaxLength(Client.AddressLineMaxLength)] string? AddressLine1,
    [MaxLength(Client.AddressLineMaxLength)] string? AddressLine2,
    [MaxLength(Client.LocalityMaxLength)] string? City,
    [MaxLength(Client.LocalityMaxLength)] string? Region,
    [MaxLength(Client.PostalCodeMaxLength)] string? PostalCode,
    [MaxLength(Client.LocalityMaxLength)] string? Country,
    [MaxLength(Supplier.AccountNumberMaxLength)] string? AccountNumber,
    [MaxLength(Supplier.NotesMaxLength)] string? Notes,
    SupplierStatus? Status);

public sealed class SupplierListQuery : PageQuery, IValidatableObject
{
    /// <summary>Part of the name or contact name, ignoring case.</summary>
    [MaxLength(Supplier.NameMaxLength)]
    public string? Q { get; init; }

    public string? Status { get; init; }

    public SupplierStatus? ParsedStatus => StrictEnum.TryParse<SupplierStatus>(Status, out var status) ? status : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<SupplierStatus>()}.", [nameof(Status)]);
        }
    }
}

// ---------- Purchase orders ----------

public sealed record PurchaseOrderSupplierSummary(Guid Id, string Name);

public sealed record PurchaseOrderItemResponse(
    Guid Id,
    int Position,
    StockProductSummary Product,
    decimal Quantity,
    decimal UnitCost,
    decimal LineTotal,
    decimal QuantityReceived,
    decimal QuantityOutstanding)
{
    /// <summary>The product must be loaded.</summary>
    public static PurchaseOrderItemResponse From(PurchaseOrderItem i) =>
        new(
            i.Id, i.Position, new StockProductSummary(i.Product!.Id, i.Product.Sku, i.Product.Name, i.Product.Unit),
            Currencies.Shortest(i.Quantity), Currencies.Shortest(i.UnitCost), Currencies.Shortest(i.LineTotal),
            Currencies.Shortest(i.QuantityReceived), Currencies.Shortest(i.QuantityOutstanding));
}

/// <summary>A purchase order as returned by the API (docs/API.md section 11.4). Amounts are in <c>currency</c>.</summary>
public sealed record PurchaseOrderResponse(
    Guid Id,
    int? Number,
    PurchaseOrderStatus Status,
    PurchaseOrderSupplierSummary? Supplier,
    string Currency,
    string? Notes,
    IReadOnlyList<PurchaseOrderItemResponse> Items,
    decimal Total,
    DateTimeOffset? OrderedAt,
    AppointmentPersonSummary? OrderedBy,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? CancelledAt,
    AppointmentPersonSummary? CancelledBy,
    string? CancelReason,
    DateTimeOffset? ClosedAt,
    AppointmentPersonSummary? ClosedBy,
    string? CloseReason,
    AppointmentPersonSummary? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Supplier, lines with their products and the people must be loaded (a missing one shows null).</summary>
    public static PurchaseOrderResponse From(PurchaseOrder o, TimeZoneInfo zone) =>
        new(
            o.Id, o.Number, o.Status, o.Supplier is { } s ? new PurchaseOrderSupplierSummary(s.Id, s.Name) : null, o.Currency, o.Notes,
            o.Items.OrderBy(i => i.Position).Select(PurchaseOrderItemResponse.From).ToList(), Currencies.Shortest(o.Total),
            Local(o.OrderedAt, zone), Person(o.OrderedBy), Local(o.ReceivedAt, zone), Local(o.CancelledAt, zone), Person(o.CancelledBy),
            o.CancelReason, Local(o.ClosedAt, zone), Person(o.ClosedBy), o.CloseReason, Person(o.CreatedBy), o.CreatedAt, o.UpdatedAt);

    private static AppointmentPersonSummary? Person(Identity.User? user) =>
        user is null ? null : new AppointmentPersonSummary(user.Id, user.Name);

    private static DateTimeOffset? Local(DateTimeOffset? instant, TimeZoneInfo zone) =>
        instant is { } value ? TimeZoneInfo.ConvertTime(value, zone) : null;
}

/// <summary>A new draft for an active supplier; the clinic currency must be set.</summary>
public sealed record CreatePurchaseOrderRequest(
    [Required] Guid? SupplierId,
    [MaxLength(PurchaseOrder.NotesMaxLength)] string? Notes);

/// <summary>Edits a draft. Omitted fields are unchanged; <c>notes: ""</c> clears the notes.</summary>
public sealed record UpdatePurchaseOrderRequest(
    Guid? SupplierId,
    [MaxLength(PurchaseOrder.NotesMaxLength)] string? Notes);

/// <summary>A new line: an active stocked product (not already on the order), the quantity and the unit cost before tax.</summary>
public sealed record CreatePurchaseOrderItemRequest(
    [Required] Guid? ProductId,
    [Required] decimal? Quantity,
    [Required] decimal? UnitCost);

/// <summary>Partial update of a draft's line.</summary>
public sealed record UpdatePurchaseOrderItemRequest(decimal? Quantity, decimal? UnitCost);

public sealed record PurchaseOrderReasonRequest([Required, MaxLength(PurchaseOrder.ReasonMaxLength)] string? Reason);

/// <summary>One delivered line: which order line, how much, and the lot it goes into (B-19 lot rules).</summary>
public sealed record ReceivePurchaseOrderLine(
    [Required] Guid? ItemId,
    [Required] decimal? Quantity,
    [MaxLength(StockLot.LotNumberMaxLength)] string? LotNumber,
    DateOnly? ExpiresOn,
    [MaxLength(StockLot.LocationMaxLength)] string? Location);

/// <summary>A delivery against an order: one or more lines; each becomes a Receive movement.</summary>
public sealed record ReceivePurchaseOrderRequest(
    [Required, MinLength(1), MaxLength(PurchaseOrder.MaxItems)] IReadOnlyList<ReceivePurchaseOrderLine>? Lines,
    [MaxLength(StockMovement.ReferenceMaxLength)] string? Reference,
    [MaxLength(StockMovement.NotesMaxLength)] string? Notes,
    DateTimeOffset? OccurredAt);

public sealed class PurchaseOrderListQuery : PageQuery, IValidatableObject
{
    public string? Status { get; init; }

    public Guid? SupplierId { get; init; }

    /// <summary>Orders with a line for this product.</summary>
    public Guid? ProductId { get; init; }

    public PurchaseOrderStatus? ParsedStatus => StrictEnum.TryParse<PurchaseOrderStatus>(Status, out var status) ? status : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<PurchaseOrderStatus>()}.", [nameof(Status)]);
        }
    }
}
