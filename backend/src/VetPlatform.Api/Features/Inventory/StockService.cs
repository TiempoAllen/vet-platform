using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Billing;
using VetPlatform.Api.Features.Consultations;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;
using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Features.Inventory;

/// <summary>
/// Stock lots and stock movements (docs/API.md section 11, ADR-019, ADR-020). A lot's quantity changes only together
/// with a movement, in one transaction that first locks the product row (after the clinic-filtered lookup), so movements
/// of a product are applied one after the other, a new lot is never created twice and a lot never goes below 0 (SRS
/// Q-5). Audit metadata holds ids, types, quantities and costs, never lot numbers, locations, references, reasons or notes.
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
        var movements = db.StockMovements.AsNoTracking();
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

        return await PageAsync(movements.OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Id), query, cancellationToken);
    }

    /// <summary>
    /// A product's register: every movement oldest first in the order recorded, with each lot's balance after it. For
    /// controlled drugs this is the controlled-drug register.
    /// </summary>
    public async Task<PagedResponse<StockMovementResponse>> RegisterAsync(Guid productId, PageQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Products.AsNoTracking().AnyAsync(p => p.Id == productId, cancellationToken))
        {
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The product was not found.");
        }

        var movements = db.StockMovements.AsNoTracking().Where(m => m.ProductId == productId);
        return await PageAsync(movements.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id), query, cancellationToken);
    }

    /// <summary>Records one movement of any type but <see cref="StockMovementType.Reversal"/> (see <see cref="ReverseAsync"/>).</summary>
    public async Task<StockMovementResponse> RecordAsync(RecordStockMovementRequest request, CancellationToken cancellationToken)
    {
        var clinic = await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken);
        var zone = ClinicTimeZone.Of(clinic);
        var now = Now;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var type = request.Type!.Value;

        // Body rules first (422 before anything is read), then the caller's permission for the type (403).
        var problems = new List<ApiErrorDetail>();
        CheckBody(request, type, clinic, today, now, problems);
        ThrowIfAny(problems);
        var roles = Permissions.RolesOf(httpContextAccessor.HttpContext!.User).ToList();
        if (!Permissions.Grant(roles, Permissions.InventoryManage)
            && !(type == StockMovementType.Dispense && Permissions.Grant(roles, Permissions.InventoryDispense)))
        {
            throw Forbidden($"Recording {type} movements needs inventory.manage.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (product, lot, newLot) = type == StockMovementType.Receive
            ? await ReceivingLotAsync(request, cancellationToken)
            : await ExistingLotAsync(request.StockLotId!.Value, request.ProductId, cancellationToken);

        // Rules that depend on the product, the lot and the referenced records, reported together.
        if (product.Controlled)
        {
            if (type == StockMovementType.Dispense && !Permissions.Grant(roles, Permissions.InventoryDispenseControlled))
            {
                throw Forbidden("Only veterinarians dispense controlled drugs.");
            }

            if (type is StockMovementType.Dispense or StockMovementType.Sell && NullIfBlank(request.Reason) is null)
            {
                problems.Add(new ApiErrorDetail("reason", "Required for controlled drugs."));
            }

            if (StockMovement.NeedsWitnessWhenControlled(type) && request.WitnessUserId is null)
            {
                problems.Add(new ApiErrorDetail("witnessUserId", "A second staff member must witness this for a controlled drug."));
            }
        }

        if (request.WitnessUserId is { } witnessId)
        {
            await CheckWitnessAsync(witnessId, problems, cancellationToken);
        }

        switch (type)
        {
            case StockMovementType.Dispense:
                await CheckPatientAsync(request.PatientId!.Value, request.ConsultationId, problems, cancellationToken);
                break;
            case StockMovementType.Sell when request.InvoiceItemId is { } itemId:
                if (!await db.Invoices.Where(i => i.Status == InvoiceStatus.Issued)
                        .SelectMany(i => i.Items).AnyAsync(item => item.Id == itemId && item.ProductId == product.Id, cancellationToken))
                {
                    problems.Add(new ApiErrorDetail("invoiceItemId", "The line was not found, is not on an issued invoice, or bills another product."));
                }

                break;
            case StockMovementType.Expire when lot.ExpiresOn is not { } expires || expires > today:
                problems.Add(new ApiErrorDetail("stockLotId", "Only lots past their expiry date are written off as expired; use Adjust otherwise."));
                break;
        }

        ThrowIfAny(problems);

        var delta = type switch
        {
            StockMovementType.Adjust => request.Quantity!.Value,
            _ when StockMovement.IsIncoming(type) => request.Quantity!.Value,
            _ => -request.Quantity!.Value,
        };
        var movement = new StockMovement
        {
            ClinicId = product.ClinicId,
            ProductId = product.Id,
            StockLotId = lot.Id,
            Type = type,
            Quantity = delta,
            UnitCost = request.UnitCost,
            Currency = request.UnitCost is null ? null : clinic.Currency,
            Reference = NullIfBlank(request.Reference),
            Reason = NullIfBlank(request.Reason),
            Notes = NullIfBlank(request.Notes),
            PatientId = request.PatientId,
            ConsultationId = request.ConsultationId,
            InvoiceItemId = request.InvoiceItemId,
            WitnessUserId = request.WitnessUserId,
            OccurredAt = request.OccurredAt is { } given ? TimestampsInterceptor.ToDatabasePrecision(given.ToUniversalTime()) : now,
            PerformedByUserId = CallerId,
        };
        Apply(product, lot, movement);
        audit.Record(
            AuditAction(type), nameof(StockMovement), movement.Id,
            new
            {
                type, productId = product.Id, stockLotId = lot.Id, newLot, quantity = delta, balanceAfter = movement.BalanceAfter,
                controlled = product.Controlled, patientId = movement.PatientId, consultationId = movement.ConsultationId,
                invoiceItemId = movement.InvoiceItemId, witnessUserId = movement.WitnessUserId,
                unitCost = movement.UnitCost, currency = movement.Currency,
            });
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(movement.Id, zone, cancellationToken);
    }

    /// <summary>
    /// Undoes a movement recorded by mistake with the exact opposite movement on the same lot. The original stays; a
    /// movement is reversed at most once, and a reversal is never reversed.
    /// </summary>
    public async Task<StockMovementResponse> ReverseAsync(Guid id, ReverseStockMovementRequest request, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var original = await db.StockMovements.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The stock movement was not found.");
        if (original.Type == StockMovementType.Reversal)
        {
            throw Conflict("A reversal cannot be reversed; record a new movement instead.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (product, lot, _) = await ExistingLotAsync(original.StockLotId, null, cancellationToken);
        if (await db.StockMovements.AnyAsync(m => m.ReversesMovementId == id, cancellationToken))
        {
            throw Conflict("The movement has already been reversed.");
        }

        var problems = new List<ApiErrorDetail>();
        if (product.Controlled && request.WitnessUserId is null)
        {
            problems.Add(new ApiErrorDetail("witnessUserId", "A second staff member must witness this for a controlled drug."));
        }

        if (request.WitnessUserId is { } witnessId)
        {
            await CheckWitnessAsync(witnessId, problems, cancellationToken);
        }

        ThrowIfAny(problems);
        var reversal = new StockMovement
        {
            ClinicId = original.ClinicId,
            ProductId = original.ProductId,
            StockLotId = original.StockLotId,
            Type = StockMovementType.Reversal,
            Quantity = -original.Quantity,
            Reason = request.Reason!.Trim(),
            ReversesMovementId = original.Id,
            WitnessUserId = request.WitnessUserId,
            OccurredAt = Now,
            PerformedByUserId = CallerId,
        };
        Apply(product, lot, reversal);
        audit.Record(
            AuditActions.StockReversed, nameof(StockMovement), reversal.Id,
            new
            {
                reversesMovementId = original.Id, reversedType = original.Type, productId = product.Id, stockLotId = lot.Id,
                quantity = reversal.Quantity, balanceAfter = reversal.BalanceAfter, controlled = product.Controlled,
                witnessUserId = reversal.WitnessUserId,
            });
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(reversal.Id, zone, cancellationToken);
    }

    // ---------- Helpers ----------

    /// <summary>Field rules that need no lookup: which fields each type takes, ranges, decimals and dates.</summary>
    private static void CheckBody(
        RecordStockMovementRequest request, StockMovementType type, Clinic clinic, DateOnly today, DateTimeOffset now, List<ApiErrorDetail> problems)
    {
        void NotFor(string field, bool given)
        {
            if (given)
            {
                problems.Add(new ApiErrorDetail(field, $"Not used for {type} movements."));
            }
        }

        if (type == StockMovementType.Reversal)
        {
            problems.Add(new ApiErrorDetail("type", "Reverse a movement with POST /api/inventory/movements/{id}/reverse."));
            return;
        }

        var quantity = request.Quantity!.Value;
        var magnitude = Math.Abs(quantity);
        if ((type == StockMovementType.Adjust ? quantity == 0 : quantity <= 0)
            || magnitude > StockMovement.MaxQuantity || decimal.Round(quantity, 3) != quantity)
        {
            problems.Add(new ApiErrorDetail(
                "quantity",
                type == StockMovementType.Adjust
                    ? $"Not 0, at most {StockMovement.MaxQuantity} either way, with at most 3 decimals (negative takes stock out)."
                    : $"More than 0 and at most {StockMovement.MaxQuantity}, with at most 3 decimals."));
        }

        if (type == StockMovementType.Receive)
        {
            if (request.ProductId is null)
            {
                problems.Add(new ApiErrorDetail("productId", "Required for Receive movements."));
            }

            NotFor("stockLotId", request.StockLotId is not null);
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

            NotFor("witnessUserId", request.WitnessUserId is not null);
        }
        else
        {
            if (request.StockLotId is null)
            {
                problems.Add(new ApiErrorDetail("stockLotId", $"Required for {type} movements."));
            }

            NotFor("lotNumber", request.LotNumber is not null);
            NotFor("expiresOn", request.ExpiresOn is not null);
            NotFor("location", request.Location is not null);
            NotFor("unitCost", request.UnitCost is not null);
        }

        if (type == StockMovementType.Dispense)
        {
            if (request.PatientId is null)
            {
                problems.Add(new ApiErrorDetail("patientId", "Required for Dispense movements."));
            }
        }
        else
        {
            NotFor("patientId", request.PatientId is not null);
            NotFor("consultationId", request.ConsultationId is not null);
        }

        if (type != StockMovementType.Sell)
        {
            NotFor("invoiceItemId", request.InvoiceItemId is not null);
        }

        if (StockMovement.NeedsReason(type) && NullIfBlank(request.Reason) is null)
        {
            problems.Add(new ApiErrorDetail("reason", $"Required for {type} movements."));
        }

        if (request.WitnessUserId is { } witness && witness == Guid.Empty)
        {
            problems.Add(new ApiErrorDetail("witnessUserId", "Not a valid user id."));
        }

        var occurredAt = request.OccurredAt is { } given ? TimestampsInterceptor.ToDatabasePrecision(given.ToUniversalTime()) : now;
        if (occurredAt > now || occurredAt < Earliest)
        {
            problems.Add(new ApiErrorDetail("occurredAt", "Cannot be in the future or before 2000."));
        }
    }

    /// <summary>The product (locked) and the lot a receipt goes into, created when no lot matches its identity.</summary>
    private async Task<(Product Product, StockLot Lot, bool NewLot)> ReceivingLotAsync(RecordStockMovementRequest request, CancellationToken cancellationToken)
    {
        var productId = request.ProductId!.Value;
        var product = await LockProductAsync(productId, cancellationToken) ?? throw Invalid("productId", ProductNotFound);
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
        if (lot is not null)
        {
            return (product, lot, false);
        }

        lot = new StockLot
        {
            ClinicId = product.ClinicId,
            ProductId = product.Id,
            LotNumber = lotNumber,
            ExpiresOn = request.ExpiresOn,
            Location = location,
        };
        db.StockLots.Add(lot);
        return (product, lot, true);
    }

    /// <summary>An existing lot of the clinic and its product (locked first: lock order product, then lot).</summary>
    private async Task<(Product Product, StockLot Lot, bool NewLot)> ExistingLotAsync(Guid lotId, Guid? productId, CancellationToken cancellationToken)
    {
        var found = await db.StockLots.AsNoTracking().SingleOrDefaultAsync(l => l.Id == lotId, cancellationToken);
        if (found is null || (productId is { } given && given != found.ProductId))
        {
            throw Invalid("stockLotId", "The lot was not found or is not a lot of the product.");
        }

        var product = await LockProductAsync(found.ProductId, cancellationToken) ?? throw Invalid("stockLotId", "The lot was not found.");
        return (product, await db.StockLots.SingleAsync(l => l.Id == lotId, cancellationToken), false);
    }

    private async Task<Product?> LockProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        if (!await db.Products.AsNoTracking().AnyAsync(p => p.Id == productId, cancellationToken))
        {
            return null;
        }

        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM products WHERE id = {productId} AND clinic_id = {CurrentClinicId} FOR NO KEY UPDATE", cancellationToken);
        return await db.Products.SingleAsync(p => p.Id == productId, cancellationToken);
    }

    /// <summary>Applies the movement to the lot: never below 0 (SRS Q-5) and never above the lot maximum.</summary>
    private void Apply(Product product, StockLot lot, StockMovement movement)
    {
        var balance = lot.Quantity + movement.Quantity;
        if (balance < 0)
        {
            throw Conflict($"The lot holds only {Common.Money.Currencies.Shortest(lot.Quantity)} {product.Unit}; stock never goes below 0.");
        }

        if (balance > StockLot.MaxQuantity)
        {
            throw Conflict($"A lot can hold at most {StockLot.MaxQuantity} {product.Unit}.");
        }

        lot.Quantity = balance;
        movement.BalanceAfter = balance;
        db.StockMovements.Add(movement);
    }

    /// <summary>The witness must be another active staff member of the clinic.</summary>
    private async Task CheckWitnessAsync(Guid witnessId, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        if (witnessId == CallerId || !await db.Users.AnyAsync(u => u.Id == witnessId && u.Status == UserStatus.Active, cancellationToken))
        {
            problems.Add(new ApiErrorDetail("witnessUserId", "The witness must be another active staff member of the clinic."));
        }
    }

    /// <summary>An active patient of the clinic, and optionally one of its in-progress or completed consultations.</summary>
    private async Task CheckPatientAsync(Guid patientId, Guid? consultationId, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.Status == PatientStatus.Active, cancellationToken))
        {
            problems.Add(new ApiErrorDetail("patientId", "The patient was not found or is not active."));
        }
        else if (consultationId is { } id && !await ConsultationLinks.IsLinkableAsync(db, id, patientId, cancellationToken))
        {
            problems.Add(new ApiErrorDetail("consultationId", ConsultationLinks.Message));
        }
    }

    private async Task<PagedResponse<StockMovementResponse>> PageAsync(
        IQueryable<StockMovement> ordered, PageQuery query, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var page = await WithDetails(ordered).ToPageAsync(query, m => m, cancellationToken);
        var reversedBy = await ReversalsOfAsync(page.Data.Select(m => m.Id).ToList(), cancellationToken);
        return new PagedResponse<StockMovementResponse>(
            page.Data.Select(m => StockMovementResponse.From(m, reversedBy.GetValueOrDefault(m.Id), zone)).ToList(), page.Pagination);
    }

    private async Task<StockMovementResponse> GetAsync(Guid id, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var movement = await WithDetails(db.StockMovements.AsNoTracking()).SingleAsync(m => m.Id == id, cancellationToken);
        var reversedBy = await ReversalsOfAsync([id], cancellationToken);
        return StockMovementResponse.From(movement, reversedBy.GetValueOrDefault(id), zone);
    }

    private async Task<Dictionary<Guid, Guid?>> ReversalsOfAsync(List<Guid> ids, CancellationToken cancellationToken) =>
        await db.StockMovements.AsNoTracking()
            .Where(m => m.ReversesMovementId != null && ids.Contains(m.ReversesMovementId.Value))
            .ToDictionaryAsync(m => m.ReversesMovementId!.Value, m => (Guid?)m.Id, cancellationToken);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        // The lot and the movement in one save: the database checks at commit that the lot equals its movements.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
        {
            // Not expected under the product lock; kept so a collision is a retryable 409, not a 500.
            throw unique.ConstraintName == StockMovement.ReversalIndex
                ? Conflict("The movement has already been reversed.")
                : Conflict("The lot was created at the same time; try again.");
        }
    }

    private static string AuditAction(StockMovementType type) => type switch
    {
        StockMovementType.Receive => AuditActions.StockReceived,
        StockMovementType.Dispense => AuditActions.StockDispensed,
        StockMovementType.Sell => AuditActions.StockSold,
        StockMovementType.Adjust => AuditActions.StockAdjusted,
        StockMovementType.ReturnToSupplier => AuditActions.StockReturnedToSupplier,
        StockMovementType.ReturnFromClient => AuditActions.StockReturnedFromClient,
        StockMovementType.Expire => AuditActions.StockExpired,
        _ => AuditActions.StockReversed,
    };

    private const string ProductNotFound = "The product was not found or is not active.";

    private static readonly DateTimeOffset Earliest = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static IQueryable<StockMovement> WithDetails(IQueryable<StockMovement> movements) =>
        movements.Include(m => m.Product).Include(m => m.StockLot).Include(m => m.PerformedBy).Include(m => m.Witness);

    private Guid CurrentClinicId => tenant.ClinicId ?? throw new InvalidOperationException("Stock requires a current clinic.");

    private Guid CallerId =>
        AuthClaims.GetUserId(httpContextAccessor.HttpContext!.User)
        ?? throw new InvalidOperationException("Stock movements require a signed-in user.");

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

    private static ApiException Conflict(string message) => new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, message);

    private static ApiException Forbidden(string message) => new(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, message);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
