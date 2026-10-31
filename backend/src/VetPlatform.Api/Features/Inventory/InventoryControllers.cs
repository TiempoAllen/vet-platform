using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Inventory;

/// <summary>
/// The product catalogue (docs/API.md section 11, ADR-019): <c>inventory.read</c> reads (Admin, Veterinarian, Nurse,
/// Receptionist, Inventory); <c>inventory.manage</c> creates and edits (Admin, Inventory). There is no delete: deactivate.
/// </summary>
[ApiController]
[Route("api/products")]
public sealed class ProductsController(ProductService products) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.InventoryRead)]
    public Task<PagedResponse<ProductResponse>> List([FromQuery] ProductListQuery query, CancellationToken cancellationToken) =>
        products.ListAsync(query, cancellationToken);

    [HttpGet("categories")]
    [RequirePermission(Permissions.InventoryRead)]
    public Task<IReadOnlyList<string>> Categories(CancellationToken cancellationToken) => products.CategoriesAsync(cancellationToken);

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.InventoryRead)]
    public Task<ProductResponse> Get(Guid id, CancellationToken cancellationToken) => products.GetAsync(id, cancellationToken);

    [HttpPost]
    [RequirePermission(Permissions.InventoryManage)]
    public async Task<ActionResult<ProductResponse>> Create(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var product = await products.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, product);
    }

    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.InventoryManage)]
    public Task<ProductResponse> Update(Guid id, UpdateProductRequest request, CancellationToken cancellationToken) =>
        products.UpdateAsync(id, request, cancellationToken);
}

/// <summary>
/// Stock lots and movements (ADR-019): read with <c>inventory.read</c>; stock is received with <c>inventory.manage</c>.
/// Movements are never edited or deleted.
/// </summary>
[ApiController]
public sealed class StockController(StockService stock) : ControllerBase
{
    [HttpGet("api/inventory/stock")]
    [RequirePermission(Permissions.InventoryRead)]
    public Task<PagedResponse<StockLotResponse>> Stock([FromQuery] StockListQuery query, CancellationToken cancellationToken) =>
        stock.ListStockAsync(query, cancellationToken);

    [HttpGet("api/inventory/movements")]
    [RequirePermission(Permissions.InventoryRead)]
    public Task<PagedResponse<StockMovementResponse>> Movements([FromQuery] StockMovementListQuery query, CancellationToken cancellationToken) =>
        stock.ListMovementsAsync(query, cancellationToken);

    /// <summary>No single-movement GET exists (the list filters by product and lot), so no Location header.</summary>
    [HttpPost("api/inventory/movements")]
    [RequirePermission(Permissions.InventoryManage)]
    public async Task<ActionResult<StockMovementResponse>> Record(RecordStockMovementRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await stock.RecordAsync(request, cancellationToken));
}
