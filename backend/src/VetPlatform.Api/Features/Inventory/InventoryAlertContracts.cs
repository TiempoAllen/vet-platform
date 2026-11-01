using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Money;

namespace VetPlatform.Api.Features.Inventory;

public sealed record LowStockProductSummary(Guid Id, string Sku, string Name, ProductUnit Unit, ProductType Type, string? Category, bool Controlled);

/// <summary>
/// A product at or below its reorder level (docs/API.md section 11.5, ADR-022). <c>onOrder</c> is what open purchase
/// orders still await; <c>covered</c> is true when on-hand plus on-order stock is above the reorder level.
/// </summary>
public sealed record LowStockResponse(
    LowStockProductSummary Product,
    decimal ReorderLevel,
    decimal OnHand,
    decimal OnOrder,
    decimal Shortfall,
    bool Covered)
{
    public static LowStockResponse From(Product p, decimal onHand, decimal onOrder)
    {
        var level = p.ReorderLevel!.Value;
        return new(
            new LowStockProductSummary(p.Id, p.Sku, p.Name, p.Unit, p.Type, p.Category, p.Controlled),
            Currencies.Shortest(level), Currencies.Shortest(onHand), Currencies.Shortest(onOrder),
            Currencies.Shortest(level - onHand), onHand + onOrder > level);
    }
}

public sealed class LowStockQuery : PageQuery, IValidatableObject
{
    /// <summary>Exact category, ignoring case.</summary>
    [MaxLength(Product.CategoryMaxLength)]
    public string? Category { get; init; }

    public string? Type { get; init; }

    /// <summary>true: only products whose on-hand plus on-order stock is still at or below the reorder level.</summary>
    public bool? Uncovered { get; init; }

    public ProductType? ParsedType => StrictEnum.TryParse<ProductType>(Type, out var type) ? type : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Type is not null && (ParsedType is null || ParsedType == ProductType.Service))
        {
            yield return new ValidationResult("Type must be a stocked product type (not Service).", [nameof(Type)]);
        }
    }
}

/// <summary>
/// A lot holding stock that has expired or expires within the window. <c>daysLeft</c> counts clinic-local days from
/// today to the expiry date (0 on the day itself, negative once expired).
/// </summary>
public sealed record ExpiringLotResponse(StockLotResponse Lot, bool Expired, int DaysLeft)
{
    public static ExpiringLotResponse From(StockLot l, DateOnly today) =>
        new(StockLotResponse.From(l), l.ExpiresOn!.Value < today, l.ExpiresOn.Value.DayNumber - today.DayNumber);
}

public sealed class ExpiringQuery : PageQuery
{
    /// <summary>Clinic-local days ahead (0: only today and already expired).</summary>
    [Range(0, InventoryAlertsQuery.MaxDays)]
    public int WithinDays { get; init; } = InventoryAlertsQuery.DefaultDays;

    public Guid? ProductId { get; init; }
}

public sealed class InventoryAlertsQuery
{
    public const int DefaultDays = 30;
    public const int MaxDays = 365;

    /// <summary>The expiry window, as for the expiring list.</summary>
    [Range(0, MaxDays)]
    public int WithinDays { get; init; } = DefaultDays;
}

/// <summary>Counts for the dashboard (docs/API.md section 11.5): the totals of the two lists.</summary>
public sealed record InventoryAlertsResponse(int LowStock, int LowStockUncovered, int ExpiredLots, int ExpiringLots, int WithinDays, DateOnly Today);
