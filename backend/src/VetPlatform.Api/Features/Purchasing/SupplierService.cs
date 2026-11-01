using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;

namespace VetPlatform.Api.Features.Purchasing;

/// <summary>
/// The clinic's suppliers (docs/API.md section 11.4, ADR-021). Deactivated, never deleted. Audit metadata names the
/// changed fields only, never names or contact details.
/// </summary>
public sealed class SupplierService(AppDbContext db, ITenantContext tenant, AuditTrail audit)
{
    public Task<PagedResponse<SupplierResponse>> ListAsync(SupplierListQuery query, CancellationToken cancellationToken)
    {
        var suppliers = db.Suppliers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = PagingExtensions.ContainsPattern(query.Q.Trim());
            // The name column compares ignoring case, so LIKE is enough there (PostgreSQL has no ILIKE for it).
            suppliers = suppliers.Where(s =>
                EF.Functions.Like(s.Name, pattern, PagingExtensions.LikeEscape)
                || EF.Functions.ILike(s.ContactName!, pattern, PagingExtensions.LikeEscape));
        }

        if (query.ParsedStatus is { } status)
        {
            suppliers = suppliers.Where(s => s.Status == status);
        }

        return suppliers.OrderBy(s => s.Name).ThenBy(s => s.Id).ToPageAsync(query, SupplierResponse.From, cancellationToken);
    }

    public async Task<SupplierResponse> GetAsync(Guid id, CancellationToken cancellationToken) =>
        SupplierResponse.From(await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken) ?? throw NotFound());

    public async Task<SupplierResponse> CreateAsync(CreateSupplierRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw Invalid("name", "The name must not be blank.");
        }

        var supplier = new Supplier
        {
            ClinicId = tenant.ClinicId ?? throw new InvalidOperationException("Suppliers require a current clinic."),
            Name = request.Name.Trim(),
            ContactName = NullIfBlank(request.ContactName),
            Email = NullIfBlank(request.Email),
            Phone = NullIfBlank(request.Phone),
            AddressLine1 = NullIfBlank(request.AddressLine1),
            AddressLine2 = NullIfBlank(request.AddressLine2),
            City = NullIfBlank(request.City),
            Region = NullIfBlank(request.Region),
            PostalCode = NullIfBlank(request.PostalCode),
            Country = NullIfBlank(request.Country),
            AccountNumber = NullIfBlank(request.AccountNumber),
            Notes = NullIfBlank(request.Notes),
        };
        db.Suppliers.Add(supplier);
        audit.Record(AuditActions.SupplierCreated, nameof(Supplier), supplier.Id);
        await SaveAsync(cancellationToken);
        return SupplierResponse.From(supplier);
    }

    public async Task<SupplierResponse> UpdateAsync(Guid id, UpdateSupplierRequest request, CancellationToken cancellationToken)
    {
        // Body rules first, then the lookup, as elsewhere (422 before 404).
        if (request.Name is { } blank && string.IsNullOrWhiteSpace(blank))
        {
            throw Invalid("name", "The name must not be blank.");
        }

        var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken) ?? throw NotFound();
        var changed = new List<string>();

        void Text(string field, string? given, string? current, Action<string?> apply)
        {
            if (given is null)
            {
                return;
            }

            var value = NullIfBlank(given);
            if (value != current)
            {
                apply(value);
                changed.Add(field);
            }
        }

        if (request.Name is { } name && name.Trim() != supplier.Name)
        {
            supplier.Name = name.Trim();
            changed.Add("name");
        }

        Text("contactName", request.ContactName, supplier.ContactName, v => supplier.ContactName = v);
        Text("email", request.Email, supplier.Email, v => supplier.Email = v);
        Text("phone", request.Phone, supplier.Phone, v => supplier.Phone = v);
        Text("addressLine1", request.AddressLine1, supplier.AddressLine1, v => supplier.AddressLine1 = v);
        Text("addressLine2", request.AddressLine2, supplier.AddressLine2, v => supplier.AddressLine2 = v);
        Text("city", request.City, supplier.City, v => supplier.City = v);
        Text("region", request.Region, supplier.Region, v => supplier.Region = v);
        Text("postalCode", request.PostalCode, supplier.PostalCode, v => supplier.PostalCode = v);
        Text("country", request.Country, supplier.Country, v => supplier.Country = v);
        Text("accountNumber", request.AccountNumber, supplier.AccountNumber, v => supplier.AccountNumber = v);
        Text("notes", request.Notes, supplier.Notes, v => supplier.Notes = v);
        if (request.Status is { } status && status != supplier.Status)
        {
            supplier.Status = status;
            changed.Add("status");
        }

        if (changed.Count > 0)
        {
            audit.Record(AuditActions.SupplierUpdated, nameof(Supplier), id, new { fields = changed, status = supplier.Status });
            await SaveAsync(cancellationToken);
        }

        return SupplierResponse.From(supplier);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: Supplier.NameIndex })
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "A supplier with this name already exists.");
        }
    }

    private static ApiException NotFound() => new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The supplier was not found.");

    private static ApiException Invalid(string field, string message) =>
        new(StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.",
            [new ApiErrorDetail(field, message)]);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
