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

/// <summary>A product's stock register (B-20): every movement oldest first with each lot's balance after it.</summary>
[ApiController]
public sealed class ProductRegisterController(StockService stock) : ControllerBase
{
    [HttpGet("api/products/{id:guid}/register")]
    [RequirePermission(Permissions.InventoryRead)]
    public Task<PagedResponse<StockMovementResponse>> Register(Guid id, [FromQuery] PageQuery query, CancellationToken cancellationToken) =>
        stock.RegisterAsync(id, query, cancellationToken);
}

/// <summary>
/// Stock lots and movements (ADR-019, ADR-020): read with <c>inventory.read</c>. Every movement type is recorded with
/// <c>inventory.manage</c>; <c>Dispense</c> also with <c>inventory.dispense</c> (the service checks the type, and that
/// controlled drugs are dispensed only with <c>inventory.dispense_controlled</c>). Movements are never edited or
/// deleted; mistakes are reversed.
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
    [RequireAnyPermission(Permissions.InventoryManage, Permissions.InventoryDispense)]
    public async Task<ActionResult<StockMovementResponse>> Record(RecordStockMovementRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await stock.RecordAsync(request, cancellationToken));

    /// <summary>Creates the opposite movement (201); the original is kept.</summary>
    [HttpPost("api/inventory/movements/{id:guid}/reverse")]
    [RequirePermission(Permissions.InventoryManage)]
    public async Task<ActionResult<StockMovementResponse>> Reverse(Guid id, ReverseStockMovementRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await stock.ReverseAsync(id, request, cancellationToken));
}
