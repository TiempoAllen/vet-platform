using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Inventory;
using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Features.Purchasing;

/// <summary>
/// Purchase orders (docs/API.md section 11.4, ADR-021). Every change locks the order row after the clinic-filtered
/// lookup. Receiving locks the order, then the products of the delivered lines in id order, then their lots (the same
/// order a reversal of a delivery takes), and records each line as a Receive movement through <see cref="StockService"/>.
/// Audit metadata holds ids, numbers, quantities and totals, never notes or reasons.
/// </summary>
public sealed class PurchaseOrderService(
    AppDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    StockService stock,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor)
{
    public async Task<PagedResponse<PurchaseOrderResponse>> ListAsync(PurchaseOrderListQuery query, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var orders = WithDetails(db.PurchaseOrders.AsNoTracking());
        if (query.ParsedStatus is { } status)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (query.SupplierId is { } supplierId)
        {
            orders = orders.Where(o => o.SupplierId == supplierId);
        }

        if (query.ProductId is { } productId)
        {
            orders = orders.Where(o => o.Items.Any(i => i.ProductId == productId));
        }

        return await orders.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).AsSplitQuery()
            .ToPageAsync(query, o => PurchaseOrderResponse.From(o, zone), cancellationToken);
    }

    public async Task<PurchaseOrderResponse> GetAsync(Guid id, CancellationToken cancellationToken) =>
        PurchaseOrderResponse.From(await FindAsync(id, tracking: false, cancellationToken), await ZoneAsync(cancellationToken));

    public async Task<PurchaseOrderResponse> CreateAsync(CreatePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        var clinic = await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken);
        if (clinic.Currency is null)
        {
            throw Conflict("Set the clinic currency before creating purchase orders.");
        }

        var problems = new List<ApiErrorDetail>();
        await CheckSupplierAsync(request.SupplierId!.Value, problems, cancellationToken);
        ThrowIfAny(problems);
        var order = new PurchaseOrder
        {
            ClinicId = clinic.Id,
            SupplierId = request.SupplierId.Value,
            Currency = clinic.Currency,
            Notes = NullIfBlank(request.Notes),
            CreatedByUserId = CallerId,
        };
        db.PurchaseOrders.Add(order);
        audit.Record(AuditActions.PurchaseOrderCreated, nameof(PurchaseOrder), order.Id, new { supplierId = order.SupplierId, currency = order.Currency });
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(order.Id, cancellationToken);
    }

    public Task<PurchaseOrderResponse> UpdateAsync(Guid id, UpdatePurchaseOrderRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.PurchaseOrderUpdated, async order =>
        {
            RequireDraft(order);
            var changed = new List<string>();
            if (request.SupplierId is { } supplierId && supplierId != order.SupplierId)
            {
                var problems = new List<ApiErrorDetail>();
                await CheckSupplierAsync(supplierId, problems, cancellationToken);
                ThrowIfAny(problems);
                order.SupplierId = supplierId;
                order.Supplier = null;
                changed.Add("supplierId");
            }

            if (request.Notes is { } notes && NullIfBlank(notes) != order.Notes)
            {
                order.Notes = NullIfBlank(notes);
                changed.Add("notes");
            }

            return changed.Count == 0 ? null : (object)new { fields = changed, supplierId = order.SupplierId };
        }, cancellationToken);

    /// <summary>Drafts only; an ordered purchase order is cancelled or closed instead.</summary>
    public async Task DeleteDraftAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var order = await LockAsync(id, cancellationToken);
        RequireDraft(order);
        db.RemoveRange(order.Items);
        db.PurchaseOrders.Remove(order);
        audit.Record(AuditActions.PurchaseOrderDeleted, nameof(PurchaseOrder), id, new { supplierId = order.SupplierId, itemCount = order.Items.Count });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------- Lines ----------

    public Task<PurchaseOrderResponse> AddItemAsync(Guid id, CreatePurchaseOrderItemRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.PurchaseOrderItemAdded, async order =>
        {
            RequireDraft(order);
            if (order.Items.Count >= PurchaseOrder.MaxItems)
            {
                throw Conflict($"A purchase order can have at most {PurchaseOrder.MaxItems} lines.");
            }

            var productId = request.ProductId!.Value;
            var problems = new List<ApiErrorDetail>();
            if (!await db.Products.AnyAsync(p => p.Id == productId && p.Status == ProductStatus.Active && p.Type != ProductType.Service, cancellationToken))
            {
                problems.Add(new ApiErrorDetail("productId", "The product was not found, is not active, or is a service."));
            }
            else if (order.Items.Any(i => i.ProductId == productId))
            {
                problems.Add(new ApiErrorDetail("productId", "The product is already on the order; change that line instead."));
            }

            var item = new PurchaseOrderItem
            {
                ClinicId = order.ClinicId,
                PurchaseOrderId = order.Id,
                Position = order.Items.Count == 0 ? 1 : order.Items.Max(i => i.Position) + 1,
                ProductId = productId,
                Quantity = request.Quantity!.Value,
                UnitCost = request.UnitCost!.Value,
            };
            CheckLine(item, problems);
            ThrowIfAny(problems);
            order.Items.Add(item);
            db.Add(item); // a preset key would otherwise make EF treat it as an existing row
            Recompute(order);
            return (object?)new { itemId = item.Id, productId, quantity = item.Quantity, total = order.Total };
        }, cancellationToken);

    public Task<PurchaseOrderResponse> UpdateItemAsync(Guid id, Guid itemId, UpdatePurchaseOrderItemRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.PurchaseOrderItemUpdated, order =>
        {
            var item = order.Items.SingleOrDefault(i => i.Id == itemId) ?? throw NotFound("The purchase order line was not found.");
            RequireDraft(order);
            var changed = new List<string>();
            if (request.Quantity is { } quantity && quantity != item.Quantity)
            {
                item.Quantity = quantity;
                changed.Add("quantity");
            }

            if (request.UnitCost is { } unitCost && unitCost != item.UnitCost)
            {
                item.UnitCost = unitCost;
                changed.Add("unitCost");
            }

            var problems = new List<ApiErrorDetail>();
            CheckLine(item, problems);
            ThrowIfAny(problems);
            Recompute(order);
            return Task.FromResult<object?>(changed.Count == 0 ? null : new { itemId, fields = changed, total = order.Total });
        }, cancellationToken);

    public Task<PurchaseOrderResponse> RemoveItemAsync(Guid id, Guid itemId, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.PurchaseOrderItemRemoved, order =>
        {
            var item = order.Items.SingleOrDefault(i => i.Id == itemId) ?? throw NotFound("The purchase order line was not found.");
            RequireDraft(order);
            order.Items.Remove(item);
            db.Remove(item);
            Recompute(order);
            return Task.FromResult<object?>(new { itemId, total = order.Total });
        }, cancellationToken);

    // ---------- Lifecycle ----------

    /// <summary>
    /// Places a draft with the supplier: it gets the clinic's next gapless number (the sequence row is updated in this
    /// transaction; lock order: order, then sequence) and its lines are frozen.
    /// </summary>
    public Task<PurchaseOrderResponse> OrderAsync(Guid id, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.PurchaseOrderOrdered, async order =>
        {
            RequireDraft(order);
            if (order.Items.Count == 0)
            {
                throw Conflict("Add at least one line before ordering.");
            }

            if (!await db.Suppliers.AnyAsync(s => s.Id == order.SupplierId && s.Status == SupplierStatus.Active, cancellationToken))
            {
                throw Conflict("The supplier is no longer active; choose another one.");
            }

            var productIds = order.Items.Select(i => i.ProductId).ToList();
            if (await db.Products.CountAsync(p => productIds.Contains(p.Id) && p.Status == ProductStatus.Active, cancellationToken) != productIds.Count)
            {
                throw Conflict("A product on the order is no longer active; remove its line or reactivate it.");
            }

            Recompute(order);
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO purchase_order_number_sequences (clinic_id, next_number) VALUES ({order.ClinicId}, 1) ON CONFLICT (clinic_id) DO NOTHING",
                cancellationToken);
            // ToListAsync, not SingleAsync: an UPDATE ... RETURNING cannot be composed into a larger query.
            order.Number = (await db.Database.SqlQuery<int>(
                    $"UPDATE purchase_order_number_sequences SET next_number = next_number + 1 WHERE clinic_id = {order.ClinicId} RETURNING next_number - 1 AS \"Value\"")
                .ToListAsync(cancellationToken)).Single();
            order.Status = PurchaseOrderStatus.Ordered;
            order.OrderedAt = Now;
            order.OrderedByUserId = CallerId;
            order.OrderedBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken);
            return (object?)new { number = order.Number, total = order.Total, currency = order.Currency, itemCount = order.Items.Count };
        }, cancellationToken);

    /// <summary>Withdraws an ordered purchase order before anything arrived.</summary>
    public Task<PurchaseOrderResponse> CancelAsync(Guid id, PurchaseOrderReasonRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.PurchaseOrderCancelled, async order =>
        {
            switch (order.Status)
            {
                case PurchaseOrderStatus.Draft:
                    throw Conflict("A draft is deleted, not cancelled.");
                case not PurchaseOrderStatus.Ordered:
                    throw Conflict("Only an ordered purchase order with nothing received can be cancelled; close it instead.");
            }

            order.Status = PurchaseOrderStatus.Cancelled;
            order.CancelledAt = Now;
            order.CancelledByUserId = CallerId;
            order.CancelledBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken);
            order.CancelReason = request.Reason!.Trim();
            return (object?)new { number = order.Number, from = PurchaseOrderStatus.Ordered, to = order.Status };
        }, cancellationToken);

    /// <summary>Ends a partially received order: what has not arrived will not come.</summary>
    public Task<PurchaseOrderResponse> CloseAsync(Guid id, PurchaseOrderReasonRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.PurchaseOrderClosed, async order =>
        {
            if (order.Status != PurchaseOrderStatus.PartiallyReceived)
            {
                throw Conflict("Only a partially received purchase order can be closed (cancel one with nothing received).");
            }

            order.Status = PurchaseOrderStatus.Closed;
            order.ClosedAt = Now;
            order.ClosedByUserId = CallerId;
            order.ClosedBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken);
            order.CloseReason = request.Reason!.Trim();
            return (object?)new { number = order.Number, from = PurchaseOrderStatus.PartiallyReceived, to = order.Status };
        }, cancellationToken);

    /// <summary>
    /// Receives a delivery: each line becomes a Receive movement into the lot it names (B-19 lot rules), carrying the
    /// line's unit cost and the order's supplier; never more than a line still awaits.
    /// </summary>
    public async Task<PurchaseOrderResponse> ReceiveAsync(Guid id, ReceivePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        var clinic = await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken);
        var zone = ClinicTimeZone.Of(clinic);
        var now = Now;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var lines = request.Lines!;

        // Body rules first (422 before the order is read).
        var problems = new List<ApiErrorDetail>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var quantity = line.Quantity!.Value;
            if (quantity <= 0 || quantity > StockMovement.MaxQuantity || decimal.Round(quantity, 3) != quantity)
            {
                problems.Add(new ApiErrorDetail($"lines[{index}].quantity", $"More than 0 and at most {StockMovement.MaxQuantity}, with at most 3 decimals."));
            }

            if (line.ExpiresOn is { } expiresOn)
            {
                if (!ScheduleRules.IsInSupportedRange(expiresOn))
                {
                    problems.Add(new ApiErrorDetail($"lines[{index}].expiresOn", ScheduleRules.SupportedRangeMessage));
                }
                else if (expiresOn < today)
                {
                    problems.Add(new ApiErrorDetail($"lines[{index}].expiresOn", "The lot has already expired; expired stock is not received."));
                }
            }

            if (lines.Take(index).Any(other => other.ItemId == line.ItemId))
            {
                problems.Add(new ApiErrorDetail($"lines[{index}].itemId", "Each order line appears once per delivery."));
            }
        }

        var occurredAt = request.OccurredAt is { } given ? TimestampsInterceptor.ToDatabasePrecision(given.ToUniversalTime()) : now;
        if (occurredAt > now)
        {
            problems.Add(new ApiErrorDetail("occurredAt", "Cannot be in the future."));
        }

        ThrowIfAny(problems);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var order = await LockAsync(id, cancellationToken);
        if (!order.IsOpen)
        {
            throw Conflict(order.Status == PurchaseOrderStatus.Draft
                ? "Order the purchase order before receiving against it."
                : $"A {order.Status} purchase order takes no more deliveries.");
        }

        if (occurredAt < order.OrderedAt)
        {
            throw Invalid("occurredAt", "Cannot be before the order was placed.");
        }

        var received = new List<(int Index, PurchaseOrderItem Item, ReceivePurchaseOrderLine Line)>();
        for (var index = 0; index < lines.Count; index++)
        {
            var item = order.Items.SingleOrDefault(i => i.Id == lines[index].ItemId);
            if (item is null)
            {
                problems.Add(new ApiErrorDetail($"lines[{index}].itemId", "The line is not on this order."));
                continue;
            }

            received.Add((index, item, lines[index]));
        }

        ThrowIfAny(problems);
        foreach (var (index, item, line) in received)
        {
            if (line.Quantity!.Value > item.QuantityOutstanding)
            {
                throw Conflict($"Line {item.Position} awaits only {Currencies.Shortest(item.QuantityOutstanding)} {item.Product!.Unit}.");
            }
        }

        // Lock order: the order (above), then each product in id order, then lots.
        var products = new Dictionary<Guid, Product>();
        foreach (var productId in received.Select(r => r.Item.ProductId).Distinct().Order())
        {
            products[productId] = (await stock.LockProductAsync(productId, cancellationToken))!;
        }

        foreach (var (index, item, line) in received)
        {
            var product = products[item.ProductId];
            if (product.Status != ProductStatus.Active)
            {
                problems.Add(new ApiErrorDetail($"lines[{index}].itemId", "The product is no longer active; reactivate it or close the order."));
            }
            else if (product.RequiresExpiry && line.ExpiresOn is null)
            {
                problems.Add(new ApiErrorDetail($"lines[{index}].expiresOn", $"Required for {product.Type} products."));
            }
        }

        ThrowIfAny(problems);
        var reference = NullIfBlank(request.Reference);
        var notes = NullIfBlank(request.Notes);
        var movements = new List<Guid>();
        foreach (var (_, item, line) in received)
        {
            var movement = await stock.ReceiveForOrderAsync(
                products[item.ProductId], order, item, line.Quantity!.Value, NullIfBlank(line.LotNumber), line.ExpiresOn,
                NullIfBlank(line.Location), reference, notes, occurredAt, cancellationToken);
            item.QuantityReceived += line.Quantity.Value;
            movements.Add(movement.Id);
        }

        order.ApplyReceivingStatus(now);
        audit.Record(
            AuditActions.PurchaseOrderReceived, nameof(PurchaseOrder), order.Id,
            new
            {
                number = order.Number, status = order.Status, movementIds = movements,
                lines = received.Select(r => new { itemId = r.Item.Id, quantity = r.Line.Quantity }).ToList(),
            });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PurchaseOrderResponse.From(await FindAsync(id, tracking: false, cancellationToken), zone);
    }

    // ---------- Helpers ----------

    /// <summary>Locks the order row (after the clinic-filtered lookup), applies the change and audits it.</summary>
    private async Task<PurchaseOrderResponse> WriteAsync(Guid id, string auditAction, Func<PurchaseOrder, Task<object?>> apply, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var order = await LockAsync(id, cancellationToken);
        var metadata = await apply(order);
        if (metadata is not null)
        {
            audit.Record(auditAction, nameof(PurchaseOrder), order.Id, metadata);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return PurchaseOrderResponse.From(await FindAsync(id, tracking: false, cancellationToken), zone);
    }

    private async Task<PurchaseOrder> LockAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await db.PurchaseOrders.AsNoTracking().AnyAsync(o => o.Id == id, cancellationToken))
        {
            throw NotFound("The purchase order was not found.");
        }

        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM purchase_orders WHERE id = {id} AND clinic_id = {CurrentClinicId} FOR NO KEY UPDATE", cancellationToken);
        return await FindAsync(id, tracking: true, cancellationToken);
    }

    private async Task<PurchaseOrder> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var orders = WithDetails(tracking ? db.PurchaseOrders : db.PurchaseOrders.AsNoTracking());
        return await orders.AsSplitQuery().SingleOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw NotFound("The purchase order was not found.");
    }

    private static IQueryable<PurchaseOrder> WithDetails(IQueryable<PurchaseOrder> orders) =>
        orders.Include(o => o.Supplier).Include(o => o.Items).ThenInclude(i => i.Product)
            .Include(o => o.OrderedBy).Include(o => o.CancelledBy).Include(o => o.ClosedBy).Include(o => o.CreatedBy);

    private async Task CheckSupplierAsync(Guid supplierId, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        if (!await db.Suppliers.AnyAsync(s => s.Id == supplierId && s.Status == SupplierStatus.Active, cancellationToken))
        {
            problems.Add(new ApiErrorDetail("supplierId", "The supplier was not found or is not active."));
        }
    }

    private static void CheckLine(PurchaseOrderItem item, List<ApiErrorDetail> problems)
    {
        if (item.Quantity <= 0 || item.Quantity > StockMovement.MaxQuantity || decimal.Round(item.Quantity, 3) != item.Quantity)
        {
            problems.Add(new ApiErrorDetail("quantity", $"More than 0 and at most {StockMovement.MaxQuantity}, with at most 3 decimals."));
        }

        if (item.UnitCost < 0 || item.UnitCost > StockMovement.MaxUnitCost || decimal.Round(item.UnitCost, 4) != item.UnitCost)
        {
            problems.Add(new ApiErrorDetail("unitCost", $"0 or more and at most {StockMovement.MaxUnitCost}, with at most 4 decimals."));
        }
    }

    /// <summary>Line totals (quantity × unit cost, rounded half away from zero to the currency) and their sum.</summary>
    private static void Recompute(PurchaseOrder order)
    {
        var minorUnits = Currencies.MinorUnits(order.Currency);
        foreach (var item in order.Items)
        {
            item.LineTotal = Currencies.Round(item.Quantity * item.UnitCost, minorUnits);
        }

        order.Total = order.Items.Sum(i => i.LineTotal);
        if (order.Total > Billing.Invoice.MaxAmount)
        {
            throw Invalid("unitCost", $"The order total cannot exceed {Billing.Invoice.MaxAmount} {order.Currency}.");
        }
    }

    private static void RequireDraft(PurchaseOrder order)
    {
        if (order.Status != PurchaseOrderStatus.Draft)
        {
            throw Conflict($"A {order.Status} purchase order cannot be changed.");
        }
    }

    private Guid CurrentClinicId => tenant.ClinicId ?? throw new InvalidOperationException("Purchase orders require a current clinic.");

    private Guid CallerId =>
        AuthClaims.GetUserId(httpContextAccessor.HttpContext!.User)
        ?? throw new InvalidOperationException("Purchase orders require a signed-in user.");

    private DateTimeOffset Now => TimestampsInterceptor.ToDatabasePrecision(timeProvider.GetUtcNow());

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) =>
        ClinicTimeZone.Of(await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken));

    private static void ThrowIfAny(List<ApiErrorDetail> problems)
    {
        if (problems.Count > 0)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.", problems);
        }
    }

    private static ApiException Invalid(string field, string message) =>
        new(StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.",
            [new ApiErrorDetail(field, message)]);

    private static ApiException NotFound(string message) => new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);

    private static ApiException Conflict(string message) => new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, message);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
