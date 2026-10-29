using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;

namespace VetPlatform.Api.Features.Billing;

/// <summary>
/// The clinic's tax rates (docs/API.md section 10, ADR-016). Read by billing staff to pick a rate for a line; managed by
/// administrators. A rate's percentage never changes; rates are archived, never deleted.
/// </summary>
public sealed class TaxRateService(AppDbContext db, ITenantContext tenant, AuditTrail audit)
{
    public Task<PagedResponse<TaxRateResponse>> ListAsync(TaxRateListQuery query, CancellationToken cancellationToken)
    {
        var rates = db.TaxRates.AsNoTracking();
        if (query.ParsedStatus is { } status)
        {
            rates = rates.Where(t => t.Status == status);
        }

        return rates.OrderBy(t => t.Name).ThenBy(t => t.Id).ToPageAsync(query, TaxRateResponse.From, cancellationToken);
    }

    public async Task<TaxRateResponse> CreateAsync(CreateTaxRateRequest request, CancellationToken cancellationToken)
    {
        var percent = request.RatePercent!.Value;
        if (percent is < 0 or > 100 || decimal.Round(percent, 4) != percent)
        {
            throw Invalid("ratePercent", "Between 0 and 100, with at most 4 decimals.");
        }

        var rate = new TaxRate
        {
            ClinicId = tenant.ClinicId ?? throw new InvalidOperationException("Tax rates require a current clinic."),
            Name = request.Name!.Trim(),
            RatePercent = percent,
        };
        db.TaxRates.Add(rate);
        audit.Record(AuditActions.TaxRateCreated, nameof(TaxRate), rate.Id, new { ratePercent = percent });
        await SaveAsync(cancellationToken);
        return TaxRateResponse.From(rate);
    }

    public async Task<TaxRateResponse> UpdateAsync(Guid id, UpdateTaxRateRequest request, CancellationToken cancellationToken)
    {
        // Body rules first, then the lookup, as elsewhere (422 before 404).
        if (request.Name is { } blank && string.IsNullOrWhiteSpace(blank))
        {
            throw Invalid("name", "The name must not be blank.");
        }

        var rate = await db.TaxRates.SingleOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The tax rate was not found.");
        var changed = new List<string>();
        if (request.Name is { } name)
        {
            if (name.Trim() != rate.Name)
            {
                rate.Name = name.Trim();
                changed.Add("name");
            }
        }

        if (request.Status is { } status && status != rate.Status)
        {
            rate.Status = status;
            changed.Add("status");
        }

        if (changed.Count > 0)
        {
            audit.Record(AuditActions.TaxRateUpdated, nameof(TaxRate), id, new { fields = changed, status = rate.Status });
            await SaveAsync(cancellationToken);
        }

        return TaxRateResponse.From(rate);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: TaxRate.ActiveNameIndex })
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "An active tax rate with this name already exists.");
        }
    }

    private static ApiException Invalid(string field, string message) =>
        new(StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.",
            [new ApiErrorDetail(field, message)]);
}
