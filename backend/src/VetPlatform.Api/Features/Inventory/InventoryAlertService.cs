using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Purchasing;

namespace VetPlatform.Api.Features.Inventory;

/// <summary>
/// Low-stock and expiring-soon queries (docs/API.md section 11.5, ADR-022). Read-only: computed from the lots and the
/// open purchase orders at the time of the request, through the clinic query filters. Not audited (inventory reads).
/// </summary>
public sealed class InventoryAlertService(AppDbContext db, ITenantContext tenant, TimeProvider timeProvider)
{
    /// <summary>Active stocked products with a reorder level whose on-hand stock is at or below it, deepest first.</summary>
    public Task<PagedResponse<LowStockResponse>> LowStockAsync(LowStockQuery query, CancellationToken cancellationToken)
    {
        var rows = LowStockRows();
        if (query.ParsedType is { } type)
        {
            rows = rows.Where(r => r.Product.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var category = query.Category.Trim();
            rows = rows.Where(r => r.Product.Category == category);
        }

        if (query.Uncovered is true)
        {
            rows = rows.Where(r => r.OnHand + r.OnOrder <= r.Product.ReorderLevel);
        }

        return rows
            .OrderBy(r => r.OnHand - r.Product.ReorderLevel).ThenBy(r => r.Product.Name).ThenBy(r => r.Product.Id)
            .ToPageAsync(query, r => LowStockResponse.From(r.Product, r.OnHand, r.OnOrder), cancellationToken);
    }

    /// <summary>Lots holding stock whose expiry date is within the window (expired ones included), soonest first.</summary>
    public async Task<PagedResponse<ExpiringLotResponse>> ExpiringAsync(ExpiringQuery query, CancellationToken cancellationToken)
    {
        var today = await TodayAsync(cancellationToken);
        var lots = ExpiringLots(today, query.WithinDays).Include(l => l.Product).AsQueryable();
        if (query.ProductId is { } productId)
        {
            lots = lots.Where(l => l.ProductId == productId);
        }

        return await lots
            .OrderBy(l => l.ExpiresOn).ThenBy(l => l.Product!.Name).ThenBy(l => l.Id)
            .ToPageAsync(query, l => ExpiringLotResponse.From(l, today), cancellationToken);
    }

    /// <summary>The counts behind the two lists, for the dashboard.</summary>
    public async Task<InventoryAlertsResponse> AlertsAsync(InventoryAlertsQuery query, CancellationToken cancellationToken)
    {
        var today = await TodayAsync(cancellationToken);
        var low = LowStockRows();
        var expiring = ExpiringLots(today, query.WithinDays);
        return new InventoryAlertsResponse(
            await low.CountAsync(cancellationToken),
            await low.CountAsync(r => r.OnHand + r.OnOrder <= r.Product.ReorderLevel, cancellationToken),
            await expiring.CountAsync(l => l.ExpiresOn < today, cancellationToken),
            await expiring.CountAsync(l => l.ExpiresOn >= today, cancellationToken),
            query.WithinDays,
            today);
    }

    /// <summary>A member-initialised class, not a positional record: EF can filter and sort on its members afterwards.</summary>
    private sealed class LowStockRow
    {
        public required Product Product { get; init; }

        public decimal OnHand { get; init; }

        public decimal OnOrder { get; init; }
    }

    private IQueryable<LowStockRow> LowStockRows() =>
        db.Products.AsNoTracking()
            .Where(p => p.Status == ProductStatus.Active && p.Type != ProductType.Service && p.ReorderLevel != null)
            .Select(p => new LowStockRow
            {
                Product = p,
                OnHand = db.StockLots.Where(l => l.ProductId == p.Id).Sum(l => (decimal?)l.Quantity) ?? 0m,
                OnOrder = db.PurchaseOrders
                    .Where(o => o.Status == PurchaseOrderStatus.Ordered || o.Status == PurchaseOrderStatus.PartiallyReceived)
                    .SelectMany(o => o.Items)
                    .Where(i => i.ProductId == p.Id)
                    .Sum(i => (decimal?)(i.Quantity - i.QuantityReceived)) ?? 0m,
            })
            .Where(r => r.OnHand <= r.Product.ReorderLevel);

    private IQueryable<StockLot> ExpiringLots(DateOnly today, int withinDays)
    {
        var until = today.AddDays(withinDays);
        return db.StockLots.AsNoTracking().Where(l => l.Quantity > 0 && l.ExpiresOn != null && l.ExpiresOn <= until);
    }

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        var clinicId = tenant.ClinicId ?? throw new InvalidOperationException("Inventory alerts require a current clinic.");
        var zone = ClinicTimeZone.Of(await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == clinicId, cancellationToken));
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
    }
}
