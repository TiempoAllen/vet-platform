using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Suppliers, purchase orders and receiving deliveries (B-21) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class PurchasingTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private sealed record Clinic(HttpClient Admin, User AdminUser, HttpClient Stock, HttpClient Nurse, HttpClient Desk, Guid Supplier);

    private async Task<Clinic> ClinicAsync(string? currency = "EUR")
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var stock = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Inventory));
        var nurse = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Nurse));
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        if (currency is not null)
        {
            await OkAsync(await admin.PatchAsJsonAsync("/api/clinic", new { currency }));
        }

        var supplier = (await OkAsync(await stock.PostAsJsonAsync("/api/suppliers", new { name = "VetPharma" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        return new Clinic(admin, adminUser, stock, nurse, desk, supplier);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private string InAYear => DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime).AddYears(1).ToString("yyyy-MM-dd");

    private static async Task<Guid> ProductAsync(Clinic clinic, string sku, string type = "Supply", bool controlled = false) =>
        (await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/products", new { sku, name = sku, type, unit = "Each", controlled }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

    private static async Task<Guid> DraftAsync(Clinic clinic, params (Guid Product, decimal Quantity, decimal UnitCost)[] lines)
    {
        var id = (await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/purchase-orders", new { supplierId = clinic.Supplier }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        foreach (var (product, quantity, unitCost) in lines)
        {
            await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{id}/items", new { productId = product, quantity, unitCost }));
        }

        return id;
    }

    private static async Task<JsonElement> OrderedAsync(Clinic clinic, params (Guid Product, decimal Quantity, decimal UnitCost)[] lines) =>
        await OkAsync(await clinic.Stock.PostAsync($"/api/purchase-orders/{await DraftAsync(clinic, lines)}/order", null));

    private static Guid Item(JsonElement order, int index) => order.GetProperty("items")[index].GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> ReceiveAsync(Clinic clinic, Guid order, params object[] lines) =>
        clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{order}/receipts", new { lines, reference = "DN-1" });

    private static decimal Number(JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    // ---------- Suppliers ----------

    [Fact]
    public async Task Suppliers_AreManaged_AndNamesAreUniqueIgnoringCase()
    {
        var clinic = await ClinicAsync();
        var response = await clinic.Stock.PostAsJsonAsync("/api/suppliers", new
        {
            name = " Acme Vet Supplies ", contactName = "Sam Lee", email = "orders@acme.example", phone = "+44 20 1234",
            addressLine1 = "1 Dock Rd", city = "London", country = "UK", accountNumber = "C-991", notes = "Ships Tuesdays",
        });
        var created = await OkAsync(response, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal($"/api/suppliers/{id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal(
            ["id", "name", "contactName", "email", "phone", "addressLine1", "addressLine2", "city", "region", "postalCode", "country", "accountNumber", "notes", "status", "createdAt", "updatedAt"],
            created.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Acme Vet Supplies", created.GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PostAsJsonAsync("/api/suppliers", new { name = "acme vet supplies" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Stock.PostAsJsonAsync("/api/suppliers", new { name = " " })).StatusCode);
        var updated = await OkAsync(await clinic.Stock.PatchAsJsonAsync($"/api/suppliers/{id}", new { phone = "", status = "Inactive" }));
        Assert.Equal((JsonValueKind.Null, "Inactive"), (updated.GetProperty("phone").ValueKind, updated.GetProperty("status").GetString()));

        var found = await clinic.Desk.GetFromJsonAsync<JsonElement>("/api/suppliers?q=ACME");
        Assert.Equal([id], found.GetProperty("data").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()));
        Assert.Equal(2, (await clinic.Nurse.GetFromJsonAsync<JsonElement>("/api/suppliers")).GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.Single((await clinic.Nurse.GetFromJsonAsync<JsonElement>("/api/suppliers?status=Active")).GetProperty("data").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.PostAsJsonAsync("/api/suppliers", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Nurse.PatchAsJsonAsync($"/api/suppliers/{id}", new { name = "X" })).StatusCode);

        var entries = await factory.AuditEntriesAsync(id);
        Assert.Equal([AuditActions.SupplierCreated, AuditActions.SupplierUpdated], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.DoesNotContain("Acme", e.Metadata ?? ""));
    }

    // ---------- Drafts and ordering ----------

    [Fact]
    public async Task ADraft_IsBuilt_Ordered_AndThenFrozen()
    {
        var clinic = await ClinicAsync();
        var gauze = await ProductAsync(clinic, "GAUZE");
        var tape = await ProductAsync(clinic, "TAPE");
        var service = await ProductAsync(clinic, "CONSULT", "Service");
        var id = await DraftAsync(clinic, (gauze, 3, 0.3333m));

        var draft = await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{id}/items", new { productId = tape, quantity = 2, unitCost = 1.255m }));
        Assert.Equal(
            ["id", "number", "status", "supplier", "currency", "notes", "items", "total", "orderedAt", "orderedBy", "receivedAt", "cancelledAt", "cancelledBy", "cancelReason", "closedAt", "closedBy", "closeReason", "createdBy", "createdAt", "updatedAt"],
            draft.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["id", "position", "product", "quantity", "unitCost", "lineTotal", "quantityReceived", "quantityOutstanding"],
            draft.GetProperty("items")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal([1m, 2.51m], draft.GetProperty("items").EnumerateArray().Select(i => Number(i, "lineTotal")));
        Assert.Equal((3.51m, "EUR", JsonValueKind.Null), (Number(draft, "total"), draft.GetProperty("currency").GetString(), draft.GetProperty("number").ValueKind));

        var duplicate = await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{id}/items", new { productId = gauze, quantity = 1, unitCost = 1 });
        Assert.Equal("productId", Assert.Single((await ErrorOf(duplicate)).Details).Field);
        var forService = await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{id}/items", new { productId = service, quantity = 1, unitCost = 1 });
        Assert.Equal("productId", Assert.Single((await ErrorOf(forService)).Details).Field);
        var badCost = await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{id}/items", new { productId = tape, quantity = 0, unitCost = 0.00001m });
        Assert.Equal(["productId", "quantity", "unitCost"], (await ErrorOf(badCost)).Details!.Select(d => d.Field).Order());

        var ordered = await OkAsync(await clinic.Stock.PostAsync($"/api/purchase-orders/{id}/order", null));
        Assert.Equal((1, "Ordered"), (ordered.GetProperty("number").GetInt32(), ordered.GetProperty("status").GetString()));
        var second = await OrderedAsync(clinic, (gauze, 1, 1));
        Assert.Equal(2, second.GetProperty("number").GetInt32());

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{id}/items", new { productId = service, quantity = 1, unitCost = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PatchAsJsonAsync($"/api/purchase-orders/{id}", new { notes = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.DeleteAsync($"/api/purchase-orders/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PostAsync($"/api/purchase-orders/{id}/order", null)).StatusCode);

        var empty = await DraftAsync(clinic);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PostAsync($"/api/purchase-orders/{empty}/order", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await clinic.Stock.DeleteAsync($"/api/purchase-orders/{empty}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Stock.GetAsync($"/api/purchase-orders/{empty}")).StatusCode);

        var list = await clinic.Nurse.GetFromJsonAsync<JsonElement>($"/api/purchase-orders?status=Ordered&productId={tape}");
        Assert.Equal([id], list.GetProperty("data").EnumerateArray().Select(o => o.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Nurse.PostAsJsonAsync("/api/purchase-orders", new { supplierId = clinic.Supplier })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Nurse.GetAsync("/api/purchase-orders?status=Open")).StatusCode);
        Assert.Contains(AuditActions.PurchaseOrderOrdered, (await factory.AuditEntriesAsync(id)).Select(e => e.Action));
    }

    [Fact]
    public async Task Drafts_NeedTheClinicCurrency_AndAnActiveSupplier_ThenFixTheCurrency()
    {
        var noCurrency = await ClinicAsync(currency: null);
        Assert.Equal(HttpStatusCode.Conflict, (await noCurrency.Stock.PostAsJsonAsync("/api/purchase-orders", new { supplierId = noCurrency.Supplier })).StatusCode);

        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var inactive = (await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/suppliers", new { name = "Old" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Stock.PatchAsJsonAsync($"/api/suppliers/{inactive}", new { status = "Inactive" }));
        foreach (var supplierId in new[] { inactive, other.Supplier, Guid.NewGuid() })
        {
            var response = await clinic.Stock.PostAsJsonAsync("/api/purchase-orders", new { supplierId });
            Assert.Equal("supplierId", Assert.Single((await ErrorOf(response)).Details).Field);
        }

        await DraftAsync(clinic);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Admin.PatchAsJsonAsync("/api/clinic", new { currency = "USD" })).StatusCode);
    }

    // ---------- Receiving ----------

    [Fact]
    public async Task Deliveries_BecomeReceiveMovements_AndDriveTheStatus()
    {
        var clinic = await ClinicAsync();
        var gauze = await ProductAsync(clinic, "GAUZE");
        var amox = await ProductAsync(clinic, "AMX", "Medication");
        var order = await OrderedAsync(clinic, (gauze, 10, 0.5m), (amox, 4, 2));
        var id = order.GetProperty("id").GetGuid();

        var noExpiry = await ReceiveAsync(clinic, id, new { itemId = Item(order, 1), quantity = 1 });
        Assert.Equal("lines[0].expiresOn", Assert.Single((await ErrorOf(noExpiry)).Details).Field);
        Assert.Equal(HttpStatusCode.Conflict, (await ReceiveAsync(clinic, id, new { itemId = Item(order, 0), quantity = 11 })).StatusCode);
        var twice = await ReceiveAsync(clinic, id, new { itemId = Item(order, 0), quantity = 1 }, new { itemId = Item(order, 0), quantity = 1 });
        Assert.Equal("lines[1].itemId", Assert.Single((await ErrorOf(twice)).Details).Field);

        var partial = await OkAsync(await ReceiveAsync(clinic, id,
            new { itemId = Item(order, 0), quantity = 6, lotNumber = "G1" },
            new { itemId = Item(order, 1), quantity = 4, lotNumber = "A1", expiresOn = InAYear }), HttpStatusCode.Created);
        Assert.Equal("PartiallyReceived", partial.GetProperty("status").GetString());
        Assert.Equal([6m, 4m], partial.GetProperty("items").EnumerateArray().Select(i => Number(i, "quantityReceived")));
        Assert.Equal([4m, 0m], partial.GetProperty("items").EnumerateArray().Select(i => Number(i, "quantityOutstanding")));

        var movements = (await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/movements?purchaseOrderId={id}")).GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(2, movements.Count);
        Assert.All(movements, m => Assert.Equal((id, clinic.Supplier, "DN-1"), (m.GetProperty("purchaseOrderId").GetGuid(), m.GetProperty("supplierId").GetGuid(), m.GetProperty("reference").GetString())));
        Assert.Equal(0.5m, Number(movements.Single(m => m.GetProperty("product").GetProperty("id").GetGuid() == gauze), "unitCost"));

        Assert.Equal(HttpStatusCode.Conflict, (await ReceiveAsync(clinic, id, new { itemId = Item(order, 1), quantity = 1, expiresOn = InAYear })).StatusCode);
        var full = await OkAsync(await ReceiveAsync(clinic, id, new { itemId = Item(order, 0), quantity = 4, lotNumber = "G1" }), HttpStatusCode.Created);
        Assert.Equal("Received", full.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, full.GetProperty("receivedAt").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await ReceiveAsync(clinic, id, new { itemId = Item(order, 0), quantity = 1 })).StatusCode);

        var gauzeLot = Assert.Single((await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/stock?productId={gauze}")).GetProperty("data").EnumerateArray());
        Assert.Equal(10m, Number(gauzeLot, "quantity"));
        Assert.Contains(AuditActions.PurchaseOrderReceived, (await factory.AuditEntriesAsync(id)).Select(e => e.Action));
    }

    [Fact]
    public async Task CancelAndClose_FollowWhatArrived()
    {
        var clinic = await ClinicAsync();
        var gauze = await ProductAsync(clinic, "GAUZE");
        var untouched = (await OrderedAsync(clinic, (gauze, 5, 1))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{untouched}/close", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{untouched}/cancel", new { })).StatusCode);
        var cancelled = await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{untouched}/cancel", new { reason = "Ordered twice" }));
        Assert.Equal(("Cancelled", "Ordered twice"), (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("cancelReason").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await ReceiveAsync(clinic, untouched, new { itemId = Item(cancelled, 0), quantity = 1 })).StatusCode);

        var order = await OrderedAsync(clinic, (gauze, 5, 1));
        var id = order.GetProperty("id").GetGuid();
        await OkAsync(await ReceiveAsync(clinic, id, new { itemId = Item(order, 0), quantity = 2 }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{id}/cancel", new { reason = "x" })).StatusCode);
        var closed = await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{id}/close", new { reason = "Discontinued" }));
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await ReceiveAsync(clinic, id, new { itemId = Item(order, 0), quantity = 1 })).StatusCode);
        var drafted = await DraftAsync(clinic, (gauze, 1, 1));
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{drafted}/cancel", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task ReversingADelivery_ReopensItsLine()
    {
        var clinic = await ClinicAsync();
        var gauze = await ProductAsync(clinic, "GAUZE");
        var order = await OrderedAsync(clinic, (gauze, 5, 1));
        var id = order.GetProperty("id").GetGuid();
        await OkAsync(await ReceiveAsync(clinic, id, new { itemId = Item(order, 0), quantity = 5 }), HttpStatusCode.Created);
        var movement = Assert.Single((await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/movements?purchaseOrderId={id}")).GetProperty("data").EnumerateArray());

        var reversal = await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{movement.GetProperty("id").GetGuid()}/reverse", new { reason = "Wrong order" }), HttpStatusCode.Created);
        Assert.Equal((id, clinic.Supplier), (reversal.GetProperty("purchaseOrderId").GetGuid(), reversal.GetProperty("supplierId").GetGuid()));
        var reopened = await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/purchase-orders/{id}");
        Assert.Equal(("Ordered", 0m, JsonValueKind.Null), (reopened.GetProperty("status").GetString(), Number(reopened.GetProperty("items")[0], "quantityReceived"), reopened.GetProperty("receivedAt").ValueKind));

        // On a closed order the line goes down too, and the order stays closed.
        await OkAsync(await ReceiveAsync(clinic, id, new { itemId = Item(order, 0), quantity = 3 }), HttpStatusCode.Created);
        await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{id}/close", new { reason = "Rest discontinued" }));
        var later = (await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/movements?purchaseOrderId={id}&type=Receive")).GetProperty("data").EnumerateArray()
            .First(m => m.GetProperty("reversedByMovementId").ValueKind == JsonValueKind.Null).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{later}/reverse", new { reason = "Damaged" }), HttpStatusCode.Created);
        var closed = await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/purchase-orders/{id}");
        Assert.Equal(("Closed", 0m), (closed.GetProperty("status").GetString(), Number(closed.GetProperty("items")[0], "quantityReceived")));
    }

    [Fact]
    public async Task ConcurrentDeliveries_NeverExceedTheOrder()
    {
        var clinic = await ClinicAsync();
        var gauze = await ProductAsync(clinic, "GAUZE");
        var order = await OrderedAsync(clinic, (gauze, 10, 1));
        var id = order.GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ReceiveAsync(clinic, id, new { itemId = Item(order, 0), quantity = 4 })));

        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var after = await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/purchase-orders/{id}");
        Assert.Equal(8m, Number(after.GetProperty("items")[0], "quantityReceived"));
    }

    // ---------- Supplier links on movements ----------

    [Fact]
    public async Task FreeReceiptsAndReturns_MayNameAnActiveSupplier()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var gauze = await ProductAsync(clinic, "GAUZE");
        var received = await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/inventory/movements", new { type = "Receive", productId = gauze, quantity = 5, supplierId = clinic.Supplier }), HttpStatusCode.Created);
        Assert.Equal((clinic.Supplier, JsonValueKind.Null), (received.GetProperty("supplierId").GetGuid(), received.GetProperty("purchaseOrderId").ValueKind));
        var lot = received.GetProperty("lot").GetProperty("id").GetGuid();
        await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/inventory/movements", new { type = "ReturnToSupplier", stockLotId = lot, quantity = 1, reason = "Damaged", supplierId = clinic.Supplier }), HttpStatusCode.Created);
        Assert.Equal(2, (await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/movements?supplierId={clinic.Supplier}")).GetProperty("pagination").GetProperty("total").GetInt32());

        var onSale = await clinic.Stock.PostAsJsonAsync("/api/inventory/movements", new { type = "Sell", stockLotId = lot, quantity = 1, supplierId = clinic.Supplier });
        Assert.Equal("supplierId", Assert.Single((await ErrorOf(onSale)).Details).Field);
        var theirs = await clinic.Stock.PostAsJsonAsync("/api/inventory/movements", new { type = "Receive", productId = gauze, quantity = 1, supplierId = other.Supplier });
        var unknown = await clinic.Stock.PostAsJsonAsync("/api/inventory/movements", new { type = "Receive", productId = gauze, quantity = 1, supplierId = Guid.NewGuid() });
        Assert.Equal((await ErrorOf(unknown)).Details!.Single().Message, Assert.Single((await ErrorOf(theirs)).Details).Message);
    }

    // ---------- Tenancy ----------

    [Fact]
    public async Task OtherClinics_OrdersAndLines_AreUnreachable()
    {
        var mine = await ClinicAsync();
        var theirs = await ClinicAsync();
        var myProduct = await ProductAsync(mine, "MINE");
        var theirProduct = await ProductAsync(theirs, "THEIRS");
        var myOrder = await OrderedAsync(mine, (myProduct, 5, 1));
        var theirOrder = await OrderedAsync(theirs, (theirProduct, 5, 1));
        var theirId = theirOrder.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await mine.Stock.GetAsync($"/api/purchase-orders/{theirId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mine.Stock.PostAsJsonAsync($"/api/purchase-orders/{theirId}/cancel", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mine.Stock.GetAsync($"/api/suppliers/{theirs.Supplier}")).StatusCode);
        var foreignLine = await ReceiveAsync(mine, myOrder.GetProperty("id").GetGuid(), new { itemId = Item(theirOrder, 0), quantity = 1 });
        Assert.Equal("lines[0].itemId", Assert.Single((await ErrorOf(foreignLine)).Details).Field);
        var draft = await DraftAsync(mine);
        var theirProductLine = await mine.Stock.PostAsJsonAsync($"/api/purchase-orders/{draft}/items", new { productId = theirProduct, quantity = 1, unitCost = 1 });
        Assert.Equal("productId", Assert.Single((await ErrorOf(theirProductLine)).Details).Field);
        Assert.Single((await mine.Stock.GetFromJsonAsync<JsonElement>("/api/purchase-orders?status=Ordered")).GetProperty("data").EnumerateArray());
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE purchase_orders SET total = total + 1 WHERE id = {0}")]
    [InlineData("UPDATE purchase_orders SET notes = 'changed' WHERE id = {0}")]
    [InlineData("DELETE FROM purchase_orders WHERE id = {0}")]
    [InlineData("UPDATE purchase_order_items SET unit_cost = unit_cost + 1 WHERE purchase_order_id = {0}")]
    [InlineData("DELETE FROM purchase_order_items WHERE purchase_order_id = {0}")]
    [InlineData("UPDATE purchase_order_items SET quantity_received = 1 WHERE purchase_order_id = {0}")]
    [InlineData("UPDATE purchase_orders SET status = 'Received', received_at = now() WHERE id = {0}")]
    [InlineData("UPDATE purchase_orders SET status = 'Draft', number = NULL, ordered_at = NULL, ordered_by_user_id = NULL WHERE id = {0}")]
    [InlineData("TRUNCATE stock_movements, purchase_order_items, purchase_orders, purchase_order_number_sequences")]
    public async Task Database_FreezesPlacedOrders_AndKeepsReceiptsExact(string sql)
    {
        var clinic = await ClinicAsync();
        var gauze = await ProductAsync(clinic, "GAUZE");
        var id = (await OrderedAsync(clinic, (gauze, 5, 1))).GetProperty("id").GetGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Database_RefusesDeliveriesFromAnotherSupplier_OrOnCancelledOrders()
    {
        var clinic = await ClinicAsync();
        var gauze = await ProductAsync(clinic, "GAUZE");
        var other = (await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/suppliers", new { name = "Other" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var order = await OrderedAsync(clinic, (gauze, 5, 1));
        var id = order.GetProperty("id").GetGuid();
        await OkAsync(await ReceiveAsync(clinic, id, new { itemId = Item(order, 0), quantity = 1 }), HttpStatusCode.Created);

        // A balanced extra delivery (lot, line and movement together) naming another supplier.
        const string sql =
            "UPDATE stock_lots SET quantity = quantity + 1 WHERE product_id = {0}; " +
            "UPDATE purchase_order_items SET quantity_received = quantity_received + 1 WHERE purchase_order_id = {1}; " +
            "INSERT INTO stock_movements (id, clinic_id, product_id, stock_lot_id, type, quantity, balance_after, supplier_id, purchase_order_item_id, occurred_at, performed_by_user_id, created_at, updated_at) " +
            "SELECT gen_random_uuid(), clinic_id, product_id, stock_lot_id, 'Receive', 1, balance_after + 1, {2}, purchase_order_item_id, now(), performed_by_user_id, now(), now() FROM stock_movements WHERE purchase_order_item_id IS NOT NULL AND product_id = {0}";
        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, gauze, id, other)));
        Assert.Equal("42501", exception.SqlState);
    }
}
