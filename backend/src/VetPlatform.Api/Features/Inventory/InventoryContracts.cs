using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Billing;

namespace VetPlatform.Api.Features.Inventory;

// ---------- Products ----------

public sealed record ProductTaxRateSummary(Guid Id, string Name, decimal Percent, TaxRateStatus Status);

/// <summary>
/// A product as returned by the API (docs/API.md section 11). <c>quantityOnHand</c> is the sum of its lots, in its unit;
/// null for services, which hold no stock.
/// </summary>
public sealed record ProductResponse(
    Guid Id,
    string Sku,
    string Name,
    string? Category,
    ProductType Type,
    ProductUnit Unit,
    decimal? ReorderLevel,
    decimal? DefaultUnitPrice,
    ProductTaxRateSummary? DefaultTaxRate,
    ProductStatus Status,
    decimal? QuantityOnHand,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ProductResponse From(Product p, TaxRate? rate, decimal onHand) =>
        new(
            p.Id, p.Sku, p.Name, p.Category, p.Type, p.Unit, Shortest(p.ReorderLevel), Shortest(p.DefaultUnitPrice),
            rate is null ? null : new ProductTaxRateSummary(rate.Id, rate.Name, Currencies.Shortest(rate.RatePercent), rate.Status),
            p.Status, p.HoldsStock ? Currencies.Shortest(onHand) : null, p.CreatedAt, p.UpdatedAt);

    internal static decimal? Shortest(decimal? value) => value is { } v ? Currencies.Shortest(v) : null;
}

/// <summary>
/// A new product. <c>reorderLevel</c> is not allowed on services; <c>defaultTaxRateId</c> must be an active rate of
/// the clinic; <c>defaultUnitPrice</c> is in the clinic currency.
/// </summary>
public sealed record CreateProductRequest(
    [Required, MaxLength(Product.SkuMaxLength)] string? Sku,
    [Required, MaxLength(Product.NameMaxLength)] string? Name,
    [MaxLength(Product.CategoryMaxLength)] string? Category,
    [Required] ProductType? Type,
    [Required] ProductUnit? Unit,
    decimal? ReorderLevel,
    decimal? DefaultUnitPrice,
    Guid? DefaultTaxRateId);

/// <summary>
/// Partial update. Omitted fields are unchanged; <c>category: ""</c> clears the category; <c>null</c> clears the reorder
/// level, the default price or the default tax rate. The type and unit cannot change once the product has stock lots.
/// </summary>
public sealed record UpdateProductRequest(
    [MaxLength(Product.SkuMaxLength)] string? Sku,
    [MaxLength(Product.NameMaxLength)] string? Name,
    [MaxLength(Product.CategoryMaxLength)] string? Category,
    ProductType? Type,
    ProductUnit? Unit,
    Optional<decimal?> ReorderLevel,
    Optional<decimal?> DefaultUnitPrice,
    Optional<Guid?> DefaultTaxRateId,
    ProductStatus? Status);

public sealed class ProductListQuery : PageQuery, IValidatableObject
{
    /// <summary>Part of the name or SKU, ignoring case.</summary>
    [MaxLength(Product.NameMaxLength)]
    public string? Q { get; init; }

    public string? Type { get; init; }

    /// <summary>Active or Inactive; without it both are listed.</summary>
    public string? Status { get; init; }

    /// <summary>Exact category, ignoring case.</summary>
    [MaxLength(Product.CategoryMaxLength)]
    public string? Category { get; init; }

    public ProductType? ParsedType => StrictEnum.TryParse<ProductType>(Type, out var type) ? type : null;

    public ProductStatus? ParsedStatus => StrictEnum.TryParse<ProductStatus>(Status, out var status) ? status : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Type is not null && ParsedType is null)
        {
            yield return new ValidationResult($"Type must be one of {StrictEnum.Names<ProductType>()}.", [nameof(Type)]);
        }

        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<ProductStatus>()}.", [nameof(Status)]);
        }
    }
}

// ---------- Stock ----------

public sealed record StockProductSummary(Guid Id, string Sku, string Name, ProductUnit Unit);

/// <summary>A stock lot (docs/API.md section 11). <c>quantity</c> is on hand now, in the product's unit.</summary>
public sealed record StockLotResponse(
    Guid Id,
    StockProductSummary Product,
    string? LotNumber,
    DateOnly? ExpiresOn,
    string? Location,
    decimal Quantity,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The product must be loaded.</summary>
    public static StockLotResponse From(StockLot l) =>
        new(l.Id, Summary(l.Product!), l.LotNumber, l.ExpiresOn, l.Location, Currencies.Shortest(l.Quantity), l.CreatedAt, l.UpdatedAt);

    internal static StockProductSummary Summary(Product p) => new(p.Id, p.Sku, p.Name, p.Unit);
}

public sealed class StockListQuery : PageQuery
{
    public Guid? ProductId { get; init; }

    /// <summary>true also lists lots with nothing left; by default only lots holding stock are listed.</summary>
    public bool? IncludeEmpty { get; init; }
}

public sealed record StockMovementLotSummary(Guid Id, string? LotNumber, DateOnly? ExpiresOn, string? Location, decimal Quantity);

/// <summary>A recorded stock movement. <c>lot.quantity</c> is the lot's quantity now, not at the time of the movement.</summary>
public sealed record StockMovementResponse(
    Guid Id,
    StockMovementType Type,
    StockProductSummary Product,
    StockMovementLotSummary Lot,
    decimal Quantity,
    decimal? UnitCost,
    string? Currency,
    string? Reference,
    string? Notes,
    DateTimeOffset OccurredAt,
    AppointmentPersonSummary? PerformedBy,
    DateTimeOffset CreatedAt)
{
    /// <summary>The product, lot and person must be loaded (a missing person shows null).</summary>
    public static StockMovementResponse From(StockMovement m, TimeZoneInfo zone) =>
        new(
            m.Id, m.Type, StockLotResponse.Summary(m.Product!),
            new StockMovementLotSummary(m.StockLot!.Id, m.StockLot.LotNumber, m.StockLot.ExpiresOn, m.StockLot.Location, Currencies.Shortest(m.StockLot.Quantity)),
            Currencies.Shortest(m.Quantity), ProductResponse.Shortest(m.UnitCost), m.Currency, m.Reference, m.Notes,
            TimeZoneInfo.ConvertTime(m.OccurredAt, zone),
            m.PerformedBy is { } user ? new AppointmentPersonSummary(user.Id, user.Name) : null,
            m.CreatedAt);
}

/// <summary>
/// Records a stock movement. B-19 accepts only <c>Receive</c>: stock arriving into the lot identified by
/// <c>lotNumber</c>, <c>expiresOn</c> and <c>location</c> (an existing matching lot is topped up, otherwise a lot is
/// created). Medications and vaccines need <c>expiresOn</c>. <c>unitCost</c> is in the clinic currency.
/// </summary>
public sealed record RecordStockMovementRequest(
    [Required] StockMovementType? Type,
    [Required] Guid? ProductId,
    [Required] decimal? Quantity,
    [MaxLength(StockLot.LotNumberMaxLength)] string? LotNumber,
    DateOnly? ExpiresOn,
    [MaxLength(StockLot.LocationMaxLength)] string? Location,
    decimal? UnitCost,
    [MaxLength(StockMovement.ReferenceMaxLength)] string? Reference,
    [MaxLength(StockMovement.NotesMaxLength)] string? Notes,
    DateTimeOffset? OccurredAt);

public sealed class StockMovementListQuery : PageQuery, IValidatableObject
{
    public Guid? ProductId { get; init; }

    public Guid? StockLotId { get; init; }

    public string? Type { get; init; }

    public StockMovementType? ParsedType => StrictEnum.TryParse<StockMovementType>(Type, out var type) ? type : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Type is not null && ParsedType is null)
        {
            yield return new ValidationResult($"Type must be one of {StrictEnum.Names<StockMovementType>()}.", [nameof(Type)]);
        }
    }
}
