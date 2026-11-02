using VetPlatform.Api.Features.Inventory;

namespace VetPlatform.Api.Features.Reports;

public sealed record ReportProductSummary(Guid Id, string Sku, string Name, string? Category, ProductType Type, ProductUnit Unit, ProductStatus Status);

// ---------- Stock valuation (B-24, ADR-024) ----------

/// <summary>
/// One product's stock on hand and its value. A lot is valued at the weighted average unit cost of its costed receipts
/// (reversed receipts left out); a lot without any is uncosted and adds nothing to <c>value</c>. <c>unitCost</c> is
/// the value per costed unit (4 decimals), null when nothing is costed.
/// </summary>
public sealed record StockValuationProduct(
    ReportProductSummary Product,
    int Lots,
    int UncostedLots,
    decimal OnHand,
    decimal CostedQuantity,
    decimal UncostedQuantity,
    decimal Value,
    decimal? UnitCost);

/// <summary><c>category</c> is null for products without one.</summary>
public sealed record StockValuationCategory(string? Category, int Products, int UncostedLots, decimal Value);

public sealed record StockValuationTotals(int Products, int Lots, int UncostedLots, decimal Value);

public sealed record StockValuationReportResponse(
    DateTimeOffset AsOf,
    string? Currency,
    StockValuationTotals Totals,
    IReadOnlyList<StockValuationCategory> ByCategory,
    IReadOnlyList<StockValuationProduct> Products);

// ---------- Stock movement summary ----------

/// <summary>
/// One product's movements in the period, in its unit. Incoming columns (<c>received</c>, <c>returnedFromClient</c>)
/// and outgoing ones (<c>dispensed</c>, <c>sold</c>, <c>returnedToSupplier</c>, <c>expired</c>) are positive amounts;
/// <c>adjusted</c> is signed. A reversal counts against the type it undoes, so a mistaken dispense reversed in the same
/// period nets to 0. <c>closing = opening + net</c>.
/// </summary>
public sealed record StockMovementSummary(
    ReportProductSummary Product,
    decimal Opening,
    decimal Received,
    decimal ReturnedFromClient,
    decimal Dispensed,
    decimal Sold,
    decimal ReturnedToSupplier,
    decimal Expired,
    decimal Adjusted,
    decimal Net,
    decimal Closing);

public sealed record StockMovementsReportResponse(DateOnly From, DateOnly To, string TimeZone, IReadOnlyList<StockMovementSummary> Products);
