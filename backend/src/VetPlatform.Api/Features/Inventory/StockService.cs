using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Features.Inventory;

/// <summary>
/// Stock lots and stock movements (docs/API.md section 11, ADR-019). A lot's quantity changes only together with a
/// movement, in one transaction that first locks the product row (after the clinic-filtered lookup), so receipts of a
/// product are applied one after the other and a lot is never created twice. Audit metadata holds ids, quantities and
/// costs, never lot numbers, locations, references or notes.
/// </summary>
public sealed class StockService(
    AppDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor)
{
    public Task<PagedResponse<StockLotResponse>> ListStockAsync(StockListQuery query, CancellationToken cancellationToken)
    {
        var lots = db.StockLots.AsNoTracking().Include(l => l.Product).AsQueryable();
        if (query.ProductId is { } productId)
        {
            lots = lots.Where(l => l.ProductId == productId);
        }

        if (query.IncludeEmpty is not true)
        {
            lots = lots.Where(l => l.Quantity > 0);
        }

        // By product, then the lot that expires first (lots without expiry last).
        return lots
            .OrderBy(l => l.Product!.Name).ThenBy(l => l.ProductId)
            .ThenBy(l => l.ExpiresOn == null).ThenBy(l => l.ExpiresOn).ThenBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .ToPageAsync(query, StockLotResponse.From, cancellationToken);
    }

    public async Task<PagedResponse<StockMovementResponse>> ListMovementsAsync(StockMovementListQuery query, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var movements = WithDetails(db.StockMovements.AsNoTracking());
        if (query.ProductId is { } productId)
        {
            movements = movements.Where(m => m.ProductId == productId);
        }

        if (query.StockLotId is { } lotId)
        {
            movements = movements.Where(m => m.StockLotId == lotId);
        }

        if (query.ParsedType is { } type)
        {
            movements = movements.Where(m => m.Type == type);
        }

        return await movements
            .OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Id)
            .ToPageAsync(query, m => StockMovementResponse.From(m, zone), cancellationToken);
    }

    /// <summary>Receives stock into the matching lot of the product, creating the lot when there is none.</summary>
    public async Task<StockMovementResponse> RecordAsync(RecordStockMovementRequest request, CancellationToken cancellationToken)
    {
        var clinic = await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken);
        var zone = ClinicTimeZone.Of(clinic);
        var now = TimestampsInterceptor.ToDatabasePrecision(timeProvider.GetUtcNow());
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        // Body rules first (422 before the product is read).
        var problems = new List<ApiErrorDetail>();
        var quantity = request.Quantity!.Value;
        if (quantity <= 0 || quantity > StockMovement.MaxQuantity || decimal.Round(quantity, 3) != quantity)
        {
            problems.Add(new ApiErrorDetail("quantity", $"More than 0 and at most {StockMovement.MaxQuantity}, with at most 3 decimals."));
        }

        if (request.ExpiresOn is { } expiresOn)
        {
            if (!ScheduleRules.IsInSupportedRange(expiresOn))
            {
                problems.Add(new ApiErrorDetail("expiresOn", ScheduleRules.SupportedRangeMessage));
            }
            else if (expiresOn < today)
            {
                problems.Add(new ApiErrorDetail("expiresOn", "The lot has already expired; expired stock is not received."));
            }
        }

        if (request.UnitCost is { } unitCost)
        {
            if (unitCost < 0 || unitCost > StockMovement.MaxUnitCost || decimal.Round(unitCost, 4) != unitCost)
            {
                problems.Add(new ApiErrorDetail("unitCost", $"0 or more and at most {StockMovement.MaxUnitCost}, with at most 4 decimals."));
            }
            else if (clinic.Currency is null)
            {
                problems.Add(new ApiErrorDetail("unitCost", "Set the clinic currency before recording costs."));
            }
        }

        var occurredAt = request.OccurredAt is { } given ? TimestampsInterceptor.ToDatabasePrecision(given.ToUniversalTime()) : now;
        if (occurredAt > now || occurredAt < Earliest)
        {
            problems.Add(new ApiErrorDetail("occurredAt", "Cannot be in the future or before 2000."));
        }

        ThrowIfAny(problems);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var productId = request.ProductId!.Value;
        if (!await db.Products.AsNoTracking().AnyAsync(p => p.Id == productId, cancellationToken))
        {
            throw Invalid("productId", ProductNotFound);
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM products WHERE id = {productId} AND clinic_id = {clinic.Id} FOR NO KEY UPDATE", cancellationToken);
        var product = await db.Products.SingleAsync(p => p.Id == productId, cancellationToken);
        if (product.Status != ProductStatus.Active)
        {
            throw Invalid("productId", ProductNotFound);
        }

        if (!product.HoldsStock)
        {
            throw Invalid("productId", "Services hold no stock.");
        }

        if (product.RequiresExpiry && request.ExpiresOn is null)
        {
            throw Invalid("expiresOn", $"Required for {product.Type} products.");
        }

        var lotNumber = NullIfBlank(request.LotNumber);
        var location = NullIfBlank(request.Location);
        // The lot number and location columns compare ignoring case, as the identity index does.
        var lot = await db.StockLots.SingleOrDefaultAsync(
            l => l.ProductId == productId && l.LotNumber == lotNumber && l.ExpiresOn == request.ExpiresOn && l.Location == location,
            cancellationToken);
        var newLot = lot is null;
        if (lot is null)
        {
            lot = new StockLot
            {
                ClinicId = product.ClinicId,
                ProductId = product.Id,
                LotNumber = lotNumber,
                ExpiresOn = request.ExpiresOn,
                Location = location,
            };
            db.StockLots.Add(lot);
        }

        if (lot.Quantity + quantity > StockLot.MaxQuantity)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict, ErrorCodes.Conflict, $"A lot can hold at most {StockLot.MaxQuantity} {product.Unit}.");
        }

        lot.Quantity += quantity;
        var movement = new StockMovement
        {
            ClinicId = product.ClinicId,
            ProductId = product.Id,
            StockLotId = lot.Id,
            Type = request.Type!.Value,
            Quantity = quantity,
            UnitCost = request.UnitCost,
            Currency = request.UnitCost is null ? null : clinic.Currency,
            Reference = NullIfBlank(request.Reference),
            Notes = NullIfBlank(request.Notes),
            OccurredAt = occurredAt,
            PerformedByUserId = CallerId,
        };
        db.StockMovements.Add(movement);
        audit.Record(
            AuditActions.StockReceived, nameof(StockMovement), movement.Id,
            new { productId, stockLotId = lot.Id, newLot, quantity, unitCost = movement.UnitCost, currency = movement.Currency });

        // The lot and the movement in one save: the database checks at commit that the lot equals its movements.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: StockLot.IdentityIndex })
        {
            // Not expected under the product lock; kept so a collision is a retryable 409, not a 500.
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "The lot was created at the same time; try again.");
        }
        await transaction.CommitAsync(cancellationToken);

        var saved = await WithDetails(db.StockMovements.AsNoTracking()).SingleAsync(m => m.Id == movement.Id, cancellationToken);
        return StockMovementResponse.From(saved, zone);
    }

    private const string ProductNotFound = "The product was not found or is not active.";

    private static readonly DateTimeOffset Earliest = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static IQueryable<StockMovement> WithDetails(IQueryable<StockMovement> movements) =>
        movements.Include(m => m.Product).Include(m => m.StockLot).Include(m => m.PerformedBy);

    private Guid CurrentClinicId => tenant.ClinicId ?? throw new InvalidOperationException("Stock requires a current clinic.");

    private Guid CallerId =>
        AuthClaims.GetUserId(httpContextAccessor.HttpContext!.User)
        ?? throw new InvalidOperationException("Stock movements require a signed-in user.");

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

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
