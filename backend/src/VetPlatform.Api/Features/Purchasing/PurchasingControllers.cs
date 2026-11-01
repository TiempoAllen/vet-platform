using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Purchasing;

/// <summary>
/// Suppliers (docs/API.md section 11.4, ADR-021): <c>inventory.read</c> reads; <c>inventory.manage</c> creates and edits.
/// There is no delete: deactivate.
/// </summary>
[ApiController]
[Route("api/suppliers")]
public sealed class SuppliersController(SupplierService suppliers) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.InventoryRead)]
    public Task<PagedResponse<SupplierResponse>> List([FromQuery] SupplierListQuery query, CancellationToken cancellationToken) =>
        suppliers.ListAsync(query, cancellationToken);

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.InventoryRead)]
    public Task<SupplierResponse> Get(Guid id, CancellationToken cancellationToken) => suppliers.GetAsync(id, cancellationToken);

    [HttpPost]
    [RequirePermission(Permissions.InventoryManage)]
    public async Task<ActionResult<SupplierResponse>> Create(CreateSupplierRequest request, CancellationToken cancellationToken)
    {
        var supplier = await suppliers.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = supplier.Id }, supplier);
    }

    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.InventoryManage)]
    public Task<SupplierResponse> Update(Guid id, UpdateSupplierRequest request, CancellationToken cancellationToken) =>
        suppliers.UpdateAsync(id, request, cancellationToken);
}

/// <summary>
/// Purchase orders (ADR-021): <c>inventory.read</c> reads; <c>inventory.manage</c> edits drafts, orders, receives,
/// cancels and closes.
/// </summary>
[ApiController]
[Route("api/purchase-orders")]
public sealed class PurchaseOrdersController(PurchaseOrderService orders) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.InventoryRead)]
    public Task<PagedResponse<PurchaseOrderResponse>> List([FromQuery] PurchaseOrderListQuery query, CancellationToken cancellationToken) =>
        orders.ListAsync(query, cancellationToken);

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.InventoryRead)]
    public Task<PurchaseOrderResponse> Get(Guid id, CancellationToken cancellationToken) => orders.GetAsync(id, cancellationToken);

    [HttpPost]
    [RequirePermission(Permissions.InventoryManage)]
    public async Task<ActionResult<PurchaseOrderResponse>> Create(CreatePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await orders.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = order.Id }, order);
    }

    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.InventoryManage)]
    public Task<PurchaseOrderResponse> Update(Guid id, UpdatePurchaseOrderRequest request, CancellationToken cancellationToken) =>
        orders.UpdateAsync(id, request, cancellationToken);

    /// <summary>Drafts only.</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.InventoryManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await orders.DeleteDraftAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/items")]
    [RequirePermission(Permissions.InventoryManage)]
    public Task<PurchaseOrderResponse> AddItem(Guid id, CreatePurchaseOrderItemRequest request, CancellationToken cancellationToken) =>
        orders.AddItemAsync(id, request, cancellationToken);

    [HttpPatch("{id:guid}/items/{itemId:guid}")]
    [RequirePermission(Permissions.InventoryManage)]
    public Task<PurchaseOrderResponse> UpdateItem(Guid id, Guid itemId, UpdatePurchaseOrderItemRequest request, CancellationToken cancellationToken) =>
        orders.UpdateItemAsync(id, itemId, request, cancellationToken);

    [HttpDelete("{id:guid}/items/{itemId:guid}")]
    [RequirePermission(Permissions.InventoryManage)]
    public Task<PurchaseOrderResponse> RemoveItem(Guid id, Guid itemId, CancellationToken cancellationToken) =>
        orders.RemoveItemAsync(id, itemId, cancellationToken);

    [HttpPost("{id:guid}/order")]
    [RequirePermission(Permissions.InventoryManage)]
    public Task<PurchaseOrderResponse> Order(Guid id, CancellationToken cancellationToken) => orders.OrderAsync(id, cancellationToken);

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.InventoryManage)]
    public Task<PurchaseOrderResponse> Cancel(Guid id, PurchaseOrderReasonRequest request, CancellationToken cancellationToken) =>
        orders.CancelAsync(id, request, cancellationToken);

    [HttpPost("{id:guid}/close")]
    [RequirePermission(Permissions.InventoryManage)]
    public Task<PurchaseOrderResponse> Close(Guid id, PurchaseOrderReasonRequest request, CancellationToken cancellationToken) =>
        orders.CloseAsync(id, request, cancellationToken);

    /// <summary>Returns the order (201: stock movements were created). Movements: GET /api/inventory/movements?purchaseOrderId=.</summary>
    [HttpPost("{id:guid}/receipts")]
    [RequirePermission(Permissions.InventoryManage)]
    public async Task<ActionResult<PurchaseOrderResponse>> Receive(Guid id, ReceivePurchaseOrderRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await orders.ReceiveAsync(id, request, cancellationToken));
}
