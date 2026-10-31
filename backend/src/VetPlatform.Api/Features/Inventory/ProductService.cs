using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Billing;

namespace VetPlatform.Api.Features.Inventory;

/// <summary>
/// The clinic's product catalogue (docs/API.md section 11, ADR-019). Products are deactivated, never deleted; the type
/// and unit are fixed once stock has been received. Audit metadata names the changed fields and holds ids, enums and
/// numbers, never names, SKUs or categories.
/// </summary>
public sealed partial class ProductService(AppDbContext db, ITenantContext tenant, AuditTrail audit)
{
    public Task<PagedResponse<ProductResponse>> ListAsync(ProductListQuery query, CancellationToken cancellationToken)
    {
        var products = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = PagingExtensions.ContainsPattern(query.Q.Trim());
            // The SKU column compares ignoring case, so LIKE is enough there (PostgreSQL has no ILIKE for it).
            products = products.Where(p =>
                EF.Functions.ILike(p.Name, pattern, PagingExtensions.LikeEscape)
                || EF.Functions.Like(p.Sku, pattern, PagingExtensions.LikeEscape));
        }

        if (query.ParsedType is { } type)
        {
            products = products.Where(p => p.Type == type);
        }

        if (query.ParsedStatus is { } status)
        {
            products = products.Where(p => p.Status == status);
        }

        if (query.Controlled is { } controlled)
        {
            products = products.Where(p => p.Controlled == controlled);
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var category = query.Category.Trim();
            products = products.Where(p => p.Category == category);
        }

        return WithStock(products.OrderBy(p => p.Name).ThenBy(p => p.Id))
            .ToPageAsync(query, row => ProductResponse.From(row.Product, row.Rate, row.OnHand), cancellationToken);
    }

    public async Task<ProductResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await WithStock(db.Products.AsNoTracking().Where(p => p.Id == id)).SingleOrDefaultAsync(cancellationToken)
            ?? throw NotFound();
        return ProductResponse.From(row.Product, row.Rate, row.OnHand);
    }

    /// <summary>The categories in use (one spelling each, as the column ignores case), for a picker.</summary>
    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken cancellationToken) =>
        await db.Products.AsNoTracking()
            .Where(p => p.Category != null)
            .Select(p => p.Category!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);

    public async Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var problems = new List<ApiErrorDetail>();
        var sku = CheckSku(request.Sku!, problems);
        CheckName(request.Name!, problems);
        var product = new Product
        {
            ClinicId = tenant.ClinicId ?? throw new InvalidOperationException("Products require a current clinic."),
            Sku = sku,
            Name = request.Name!.Trim(),
            Category = NullIfBlank(request.Category),
            Type = request.Type!.Value,
            Unit = request.Unit!.Value,
            ReorderLevel = request.ReorderLevel,
            DefaultUnitPrice = request.DefaultUnitPrice,
            DefaultTaxRateId = request.DefaultTaxRateId,
            Controlled = request.Controlled ?? false,
        };
        CheckAmounts(product, problems);
        if (request.DefaultTaxRateId is { } taxRateId)
        {
            await CheckTaxRateAsync(taxRateId, problems, cancellationToken);
        }

        ThrowIfAny(problems);
        db.Products.Add(product);
        audit.Record(AuditActions.ProductCreated, nameof(Product), product.Id, new { type = product.Type, unit = product.Unit, controlled = product.Controlled });
        await SaveAsync(cancellationToken);
        return await GetAsync(product.Id, cancellationToken);
    }

    public async Task<ProductResponse> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken)
    {
        // Body rules first, then the lookup, as elsewhere (422 before 404).
        var problems = new List<ApiErrorDetail>();
        var sku = request.Sku is { } givenSku ? CheckSku(givenSku, problems) : null;
        if (request.Name is { } givenName)
        {
            CheckName(givenName, problems);
        }

        ThrowIfAny(problems);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Products.AsNoTracking().AnyAsync(p => p.Id == id, cancellationToken))
        {
            throw NotFound();
        }

        // Serialised with stock receipts of the product (lock order: product, then lot), so "has stock" stays true below.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM products WHERE id = {id} AND clinic_id = {tenant.ClinicId} FOR NO KEY UPDATE", cancellationToken);
        var product = await db.Products.SingleAsync(p => p.Id == id, cancellationToken);
        var changed = new List<string>();

        void Set<T>(string field, T current, T wanted, Action<T> apply)
        {
            if (!EqualityComparer<T>.Default.Equals(current, wanted))
            {
                apply(wanted);
                changed.Add(field);
            }
        }

        if (sku is not null) Set("sku", product.Sku, sku, v => product.Sku = v);
        if (request.Name is { } name) Set("name", product.Name, name.Trim(), v => product.Name = v);
        if (request.Category is { } category) Set("category", product.Category, NullIfBlank(category), v => product.Category = v);
        if (request.Status is { } status) Set("status", product.Status, status, v => product.Status = v);
        if (request.ReorderLevel.IsSet) Set("reorderLevel", product.ReorderLevel, request.ReorderLevel.Value, v => product.ReorderLevel = v);
        if (request.DefaultUnitPrice.IsSet) Set("defaultUnitPrice", product.DefaultUnitPrice, request.DefaultUnitPrice.Value, v => product.DefaultUnitPrice = v);
        if (request.DefaultTaxRateId.IsSet && request.DefaultTaxRateId.Value != product.DefaultTaxRateId)
        {
            if (request.DefaultTaxRateId.Value is { } taxRateId)
            {
                await CheckTaxRateAsync(taxRateId, problems, cancellationToken);
            }

            product.DefaultTaxRateId = request.DefaultTaxRateId.Value;
            changed.Add("defaultTaxRateId");
        }

        var fixedFieldsChange = (request.Type is { } type && type != product.Type) || (request.Unit is { } unit && unit != product.Unit)
            || (request.Controlled is { } flag && flag != product.Controlled);
        if (request.Type is { } newType) Set("type", product.Type, newType, v => product.Type = v);
        if (request.Unit is { } newUnit) Set("unit", product.Unit, newUnit, v => product.Unit = v);
        if (request.Controlled is { } newControlled) Set("controlled", product.Controlled, newControlled, v => product.Controlled = v);
        CheckAmounts(product, problems);
        ThrowIfAny(problems);
        if (fixedFieldsChange && await db.StockLots.AnyAsync(l => l.ProductId == id, cancellationToken))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict, ErrorCodes.Conflict, "The type, unit and controlled flag cannot change once stock has been received.");
        }

        if (changed.Count > 0)
        {
            audit.Record(AuditActions.ProductUpdated, nameof(Product), id, new { fields = changed, type = product.Type, status = product.Status, controlled = product.Controlled });
            await SaveAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    private sealed record ProductRow(Product Product, TaxRate? Rate, decimal OnHand);

    private IQueryable<ProductRow> WithStock(IQueryable<Product> products) =>
        products.Select(p => new ProductRow(
            p,
            p.DefaultTaxRate,
            db.StockLots.Where(l => l.ProductId == p.Id).Sum(l => (decimal?)l.Quantity) ?? 0m));

    private static string CheckSku(string sku, List<ApiErrorDetail> problems)
    {
        var trimmed = sku.Trim();
        if (!SkuPattern().IsMatch(trimmed))
        {
            problems.Add(new ApiErrorDetail("sku", "Letters, digits and - . _ /, starting with a letter or digit."));
        }

        return trimmed;
    }

    private static void CheckName(string name, List<ApiErrorDetail> problems)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add(new ApiErrorDetail("name", "The name must not be blank."));
        }
    }

    /// <summary>The reorder level, default price and controlled flag, against each other and the type.</summary>
    private static void CheckAmounts(Product product, List<ApiErrorDetail> problems)
    {
        if (product.Controlled && product.Type != ProductType.Medication)
        {
            problems.Add(new ApiErrorDetail("controlled", "Only medications can be controlled drugs."));
        }

        if (product.ReorderLevel is { } level)
        {
            if (!product.HoldsStock)
            {
                problems.Add(new ApiErrorDetail("reorderLevel", "Services hold no stock, so they have no reorder level."));
            }
            else if (level < 0 || level > Product.MaxReorderLevel || decimal.Round(level, 3) != level)
            {
                problems.Add(new ApiErrorDetail("reorderLevel", $"0 or more and at most {Product.MaxReorderLevel}, with at most 3 decimals."));
            }
        }

        if (product.DefaultUnitPrice is { } price
            && (price < 0 || price > InvoiceItem.MaxUnitPrice || decimal.Round(price, 4) != price))
        {
            problems.Add(new ApiErrorDetail("defaultUnitPrice", $"0 or more and at most {InvoiceItem.MaxUnitPrice}, with at most 4 decimals."));
        }
    }

    private async Task CheckTaxRateAsync(Guid taxRateId, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        if (!await db.TaxRates.AnyAsync(t => t.Id == taxRateId && t.Status == TaxRateStatus.Active, cancellationToken))
        {
            problems.Add(new ApiErrorDetail("defaultTaxRateId", "The tax rate was not found or is archived."));
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: Product.SkuIndex })
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "A product with this SKU already exists.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex SkuPattern();

    private static void ThrowIfAny(List<ApiErrorDetail> problems)
    {
        if (problems.Count > 0)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.", problems);
        }
    }

    private static ApiException NotFound() => new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The product was not found.");

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
