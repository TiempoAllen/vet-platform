using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Persistence;

namespace VetPlatform.Api.Features.Inventory;

/// <summary>
/// The shared rule for linking a clinical record to a catalogue product (B-20, ADR-020): an active product of the
/// clinic of the expected type (vaccinations: <see cref="ProductType.Vaccine"/>, prescriptions:
/// <see cref="ProductType.Medication"/>). Linking never changes stock; deactivating the product later leaves the link.
/// </summary>
public static class ProductLinks
{
    public static async Task CheckAsync(AppDbContext db, Guid productId, ProductType type, CancellationToken cancellationToken)
    {
        var problems = new List<ApiErrorDetail>();
        await CheckAsync(db, productId, type, problems, cancellationToken);
        if (problems.Count > 0)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.", problems);
        }
    }

    /// <summary>Adds the problem to <paramref name="problems"/> instead of throwing, to be reported with the others.</summary>
    public static async Task CheckAsync(
        AppDbContext db, Guid productId, ProductType type, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        if (!await db.Products.AnyAsync(p => p.Id == productId && p.Type == type && p.Status == ProductStatus.Active, cancellationToken))
        {
            problems.Add(new ApiErrorDetail("productId", $"The product was not found, is not active, or is not a {type} product."));
        }
    }
}
