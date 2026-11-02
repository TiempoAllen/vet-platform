using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Inventory;

namespace VetPlatform.Api.Features.Reports;

/// <summary>
/// Inventory reports (docs/API.md section 12, ADR-024), read with <c>reports.inventory</c>. Read-only aggregates over
/// the clinic's lots and movements through the query filters; sums are taken in the database. Products and quantities
/// only, never patients: not audited.
/// </summary>
public sealed class InventoryReportService(AppDbContext db, ITenantContext tenant, TimeProvider timeProvider)
{
    /// <summary>Stock on hand now, valued lot by lot at the weighted average cost of each lot's costed receipts.</summary>
    public async Task<StockValuationReportResponse> ValuationAsync(CancellationToken cancellationToken)
    {
        var clinic = await ReportPeriod.ClinicAsync(db, tenant, cancellationToken);
        var minorUnits = clinic.Currency is { } currency ? Currencies.MinorUnits(currency) : 2;
        var lots = await db.StockLots.AsNoTracking().Where(l => l.Quantity > 0)
            .Select(l => new { l.Id, l.ProductId, l.Quantity })
            .ToListAsync(cancellationToken);

        // Receipts that still stand (a reversed receipt never arrived) and carry a cost, per lot that holds stock.
        var costs = await db.StockMovements.AsNoTracking()
            .Where(m => m.Type == StockMovementType.Receive && m.UnitCost != null && m.StockLot!.Quantity > 0
                && !db.StockMovements.Any(r => r.ReversesMovementId == m.Id))
            .GroupBy(m => m.StockLotId)
            .Select(g => new { LotId = g.Key, Quantity = g.Sum(m => m.Quantity), Cost = g.Sum(m => m.Quantity * m.UnitCost!.Value) })
            .ToDictionaryAsync(c => c.LotId, cancellationToken);
        var productIds = lots.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToListAsync(cancellationToken);

        var valued = lots.Select(l => costs.TryGetValue(l.Id, out var c)
                ? (l.ProductId, l.Quantity, Costed: true, Value: Currencies.Round(l.Quantity * c.Cost / c.Quantity, minorUnits))
                : (l.ProductId, l.Quantity, Costed: false, Value: 0m))
            .ToLookup(l => l.ProductId);
        var rows = products
            .Select(p =>
            {
                var own = valued[p.Id].ToList();
                var costedQuantity = own.Where(l => l.Costed).Sum(l => l.Quantity);
                var value = own.Sum(l => l.Value);
                return new StockValuationProduct(
                    Summary(p), own.Count, own.Count(l => !l.Costed), Currencies.Shortest(own.Sum(l => l.Quantity)),
                    Currencies.Shortest(costedQuantity), Currencies.Shortest(own.Where(l => !l.Costed).Sum(l => l.Quantity)),
                    Currencies.Shortest(value),
                    costedQuantity == 0 ? null : Currencies.Shortest(decimal.Round(value / costedQuantity, 4, MidpointRounding.AwayFromZero)));
            })
            .OrderBy(r => r.Product.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Product.Sku, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Product.Id)
            .ToList();

        return new StockValuationReportResponse(
            TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), ClinicTimeZone.Of(clinic)), clinic.Currency,
            new StockValuationTotals(rows.Count, rows.Sum(r => r.Lots), rows.Sum(r => r.UncostedLots), Currencies.Shortest(rows.Sum(r => r.Value))),
            rows.GroupBy(r => r.Product.Category, StringComparer.OrdinalIgnoreCase)
                .Select(g => new StockValuationCategory(g.Key, g.Count(), g.Sum(r => r.UncostedLots), Currencies.Shortest(g.Sum(r => r.Value))))
                .OrderBy(c => c.Category is null).ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            rows);
    }

    /// <summary>Per product: stock at the start of the period, what came in and went out by type, and stock at its end.</summary>
    public async Task<StockMovementsReportResponse> MovementsAsync(ReportDateRange query, CancellationToken cancellationToken)
    {
        var clinic = await ReportPeriod.ClinicAsync(db, tenant, cancellationToken);
        var period = new ReportPeriod(query.From!.Value, query.To!.Value, ReportGrouping.Day, ClinicTimeZone.Of(clinic));
        var movements = db.StockMovements.AsNoTracking();
        var opening = await movements.Where(m => m.OccurredAt < period.StartsAt)
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, Quantity = g.Sum(m => m.Quantity) })
            .ToDictionaryAsync(o => o.ProductId, o => o.Quantity, cancellationToken);

        // A reversal is counted under the type of the movement it undoes.
        var changes = await (
                from m in movements
                where m.OccurredAt >= period.StartsAt && m.OccurredAt < period.EndsBefore
                join r in movements on m.ReversesMovementId equals (Guid?)r.Id into reversed
                from r in reversed.DefaultIfEmpty()
                group m.Quantity by new { m.ProductId, m.Type, ReversedType = (StockMovementType?)r.Type } into g
                select new { g.Key.ProductId, g.Key.Type, g.Key.ReversedType, Quantity = g.Sum() })
            .ToListAsync(cancellationToken);
        var byProduct = changes.ToLookup(c => c.ProductId, c => (Type: c.ReversedType ?? c.Type, c.Quantity));

        var productIds = opening.Where(o => o.Value != 0).Select(o => o.Key).Concat(changes.Select(c => c.ProductId)).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToListAsync(cancellationToken);
        var rows = products
            .Select(p =>
            {
                var own = byProduct[p.Id].ToList();
                decimal Of(StockMovementType type) => own.Where(c => c.Type == type).Sum(c => c.Quantity);
                var start = opening.GetValueOrDefault(p.Id);
                var net = own.Sum(c => c.Quantity);
                return new StockMovementSummary(
                    Summary(p), Currencies.Shortest(start), Currencies.Shortest(Of(StockMovementType.Receive)),
                    Currencies.Shortest(Of(StockMovementType.ReturnFromClient)), Currencies.Shortest(-Of(StockMovementType.Dispense)),
                    Currencies.Shortest(-Of(StockMovementType.Sell)), Currencies.Shortest(-Of(StockMovementType.ReturnToSupplier)),
                    Currencies.Shortest(-Of(StockMovementType.Expire)), Currencies.Shortest(Of(StockMovementType.Adjust)),
                    Currencies.Shortest(net), Currencies.Shortest(start + net));
            })
            .OrderBy(r => r.Product.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Product.Sku, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Product.Id)
            .ToList();
        return new StockMovementsReportResponse(period.From, period.To, period.Zone.Id, rows);
    }

    private static ReportProductSummary Summary(Product p) => new(p.Id, p.Sku, p.Name, p.Category, p.Type, p.Unit, p.Status);
}
