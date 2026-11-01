using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Low-stock and expiring-soon queries (B-22) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class InventoryAlertsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private sealed record Clinic(HttpClient Stock, HttpClient Desk, HttpClient Vet, Guid Supplier);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var stock = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Inventory));
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        var vet = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian));
        await OkAsync(await admin.PatchAsJsonAsync("/api/clinic", new { currency = "EUR" }));
        var supplier = (await OkAsync(await stock.PostAsJsonAsync("/api/suppliers", new { name = "VetPharma" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        return new Clinic(stock, desk, vet, supplier);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private DateOnly Today => DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);

    private static async Task<Guid> ProductAsync(Clinic clinic, string sku, decimal? reorderLevel, string type = "Supply", string? category = null) =>
        (await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/products", new { sku, name = sku, type, unit = "Each", reorderLevel, category }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

    private async Task<Guid> ReceiveAsync(Clinic clinic, Guid productId, decimal quantity, int? expiresInDays = null, string lotNumber = "L1") =>
        (await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/inventory/movements", new
        {
            type = "Receive", productId, quantity, lotNumber,
            expiresOn = expiresInDays is { } days ? Today.AddDays(days).ToString("yyyy-MM-dd") : null,
        }), HttpStatusCode.Created)).GetProperty("lot").GetProperty("id").GetGuid();

    private static async Task<JsonElement> OrderAsync(Clinic clinic, Guid productId, decimal quantity)
    {
        var id = (await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/purchase-orders", new { supplierId = clinic.Supplier }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{id}/items", new { productId, quantity, unitCost = 1 }));
        return await OkAsync(await clinic.Stock.PostAsync($"/api/purchase-orders/{id}/order", null));
    }

    private static decimal Number(JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    // ---------- Low stock ----------

    [Fact]
    public async Task LowStock_ListsProductsAtOrBelowTheirReorderLevel_WithWhatIsOnOrder()
    {
        var clinic = await ClinicAsync();
        var low = await ProductAsync(clinic, "LOW", 10);
        var exact = await ProductAsync(clinic, "EXACT", 10);
        var fine = await ProductAsync(clinic, "FINE", 10);
        var empty = await ProductAsync(clinic, "EMPTY", 5);
        await ProductAsync(clinic, "NOLEVEL", null);
        var inactive = await ProductAsync(clinic, "OLD", 10);
        await OkAsync(await clinic.Stock.PatchAsJsonAsync($"/api/products/{inactive}", new { status = "Inactive" }));
        await ReceiveAsync(clinic, low, 4);
        await ReceiveAsync(clinic, exact, 10);
        await ReceiveAsync(clinic, fine, 11);

        // Open orders count what they still await: 10 ordered for LOW, 3 of them received; drafts and cancelled orders do not count.
        var order = await OrderAsync(clinic, low, 10);
        await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{order.GetProperty("id").GetGuid()}/receipts",
            new { lines = new[] { new { itemId = order.GetProperty("items")[0].GetProperty("id").GetGuid(), quantity = 3 } } }), HttpStatusCode.Created);
        var cancelled = await OrderAsync(clinic, exact, 50);
        await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{cancelled.GetProperty("id").GetGuid()}/cancel", new { reason = "x" }));
        var draft = (await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/purchase-orders", new { supplierId = clinic.Supplier }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/purchase-orders/{draft}/items", new { productId = empty, quantity = 99, unitCost = 1 }));

        var page = await clinic.Desk.GetFromJsonAsync<JsonElement>("/api/inventory/low-stock");
        var rows = page.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal([empty, low, exact], rows.Select(r => r.GetProperty("product").GetProperty("id").GetGuid())); // deepest first: -5, -3, 0
        Assert.Equal(["product", "reorderLevel", "onHand", "onOrder", "shortfall", "covered"], rows[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["id", "sku", "name", "unit", "type", "category", "controlled"], rows[0].GetProperty("product").EnumerateObject().Select(p => p.Name));
        Assert.Equal((10m, 7m, 7m, 3m, true), (Number(rows[1], "reorderLevel"), Number(rows[1], "onHand"), Number(rows[1], "onOrder"), Number(rows[1], "shortfall"), rows[1].GetProperty("covered").GetBoolean()));
        Assert.Equal((0m, 0m, 5m, false), (Number(rows[0], "onHand"), Number(rows[0], "onOrder"), Number(rows[0], "shortfall"), rows[0].GetProperty("covered").GetBoolean()));
        Assert.Equal((10m, 0m, 0m, false), (Number(rows[2], "onHand"), Number(rows[2], "onOrder"), Number(rows[2], "shortfall"), rows[2].GetProperty("covered").GetBoolean()));

        var uncovered = await clinic.Vet.GetFromJsonAsync<JsonElement>("/api/inventory/low-stock?uncovered=true");
        Assert.Equal([empty, exact], uncovered.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("product").GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task LowStock_FiltersByTypeAndCategory_AndStaysInTheClinic()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var drug = await ProductAsync(clinic, "DRUG", 5, "Medication", "Antibiotics");
        var collar = await ProductAsync(clinic, "COLLAR", 5, "Retail", "Accessories");
        await ProductAsync(other, "THEIRS", 5);

        async Task<List<Guid>> Ids(string query) =>
            (await clinic.Stock.GetFromJsonAsync<JsonElement>("/api/inventory/low-stock" + query)).GetProperty("data").EnumerateArray()
                .Select(r => r.GetProperty("product").GetProperty("id").GetGuid()).ToList();

        Assert.Equal([collar, drug], await Ids(""));
        Assert.Equal([drug], await Ids("?type=Medication"));
        Assert.Equal([collar], await Ids("?category=ACCESSORIES"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Stock.GetAsync("/api/inventory/low-stock?type=Service")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Stock.GetAsync("/api/inventory/low-stock?type=Food")).StatusCode);
    }

    // ---------- Expiring ----------

    [Fact]
    public async Task Expiring_ListsLotsWithinTheWindow_ExpiredFirst()
    {
        var clinic = await ClinicAsync();
        var vaccine = await ProductAsync(clinic, "VAC", null, "Vaccine");
        var gauze = await ProductAsync(clinic, "GAUZE", null);
        var today = await ReceiveAsync(clinic, vaccine, 2, 0, "T");
        var soon = await ReceiveAsync(clinic, vaccine, 2, 10, "S");
        var later = await ReceiveAsync(clinic, vaccine, 2, 40, "L");
        await ReceiveAsync(clinic, gauze, 5); // no expiry
        var used = await ReceiveAsync(clinic, vaccine, 1, 5, "U");
        await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/inventory/movements", new { type = "Sell", stockLotId = used, quantity = 1 }), HttpStatusCode.Created);
        await OkAsync(await clinic.Stock.PatchAsJsonAsync($"/api/products/{vaccine}", new { status = "Inactive" })); // stock still on the shelf
        // An already expired lot (seeded: expired stock cannot be received).
        var expired = Guid.NewGuid();
        await factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "BEGIN; SET LOCAL session_replication_role = replica; " +
            "INSERT INTO stock_lots (id, clinic_id, product_id, lot_number, expires_on, quantity, created_at, updated_at) " +
            "SELECT {0}, clinic_id, product_id, 'X', {1}, 3, now(), now() FROM stock_lots WHERE id = {2}; COMMIT;",
            expired, Today.AddDays(-2), today));

        async Task<List<Guid>> Ids(string query) =>
            (await clinic.Vet.GetFromJsonAsync<JsonElement>("/api/inventory/expiring" + query)).GetProperty("data").EnumerateArray()
                .Select(r => r.GetProperty("lot").GetProperty("id").GetGuid()).ToList();

        var page = await clinic.Desk.GetFromJsonAsync<JsonElement>("/api/inventory/expiring");
        var rows = page.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal([expired, today, soon], rows.Select(r => r.GetProperty("lot").GetProperty("id").GetGuid()));
        Assert.Equal(["lot", "expired", "daysLeft"], rows[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal([(true, -2), (false, 0), (false, 10)], rows.Select(r => (r.GetProperty("expired").GetBoolean(), r.GetProperty("daysLeft").GetInt32())));

        Assert.Equal([expired, today], await Ids("?withinDays=0"));
        Assert.Equal([expired, today, soon, later], await Ids("?withinDays=365"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Desk.GetAsync("/api/inventory/expiring?withinDays=366")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Desk.GetAsync("/api/inventory/alerts?withinDays=-1")).StatusCode);
        Assert.Empty(await Ids($"?productId={gauze}"));

        var alerts = await clinic.Desk.GetFromJsonAsync<JsonElement>("/api/inventory/alerts?withinDays=10");
        Assert.Equal(["lowStock", "lowStockUncovered", "expiredLots", "expiringLots", "withinDays", "today"], alerts.EnumerateObject().Select(p => p.Name));
        Assert.Equal((1, 2, 10), (alerts.GetProperty("expiredLots").GetInt32(), alerts.GetProperty("expiringLots").GetInt32(), alerts.GetProperty("withinDays").GetInt32()));
        Assert.Equal(Today.ToString("yyyy-MM-dd"), alerts.GetProperty("today").GetString());
    }

    [Fact]
    public async Task Alerts_CountTheLowStockRows()
    {
        var clinic = await ClinicAsync();
        var covered = await ProductAsync(clinic, "COVERED", 10);
        await ProductAsync(clinic, "BARE", 10);
        await OrderAsync(clinic, covered, 20);

        var alerts = await clinic.Stock.GetFromJsonAsync<JsonElement>("/api/inventory/alerts");
        Assert.Equal((2, 1, 0, 0, 30), (alerts.GetProperty("lowStock").GetInt32(), alerts.GetProperty("lowStockUncovered").GetInt32(),
            alerts.GetProperty("expiredLots").GetInt32(), alerts.GetProperty("expiringLots").GetInt32(), alerts.GetProperty("withinDays").GetInt32()));
    }

    // ---------- Review fixes (B-22) ----------

    [Fact]
    public async Task OtherClinics_LotsOrdersAndCounts_NeverShow()
    {
        var mine = await ClinicAsync();
        var theirs = await ClinicAsync();
        var myProduct = await ProductAsync(mine, "MINE", 10);
        var theirProduct = await ProductAsync(theirs, "THEIRS", 10, "Vaccine");
        await ReceiveAsync(theirs, theirProduct, 2, 3);
        await OrderAsync(theirs, theirProduct, 50);

        var expiring = await mine.Stock.GetFromJsonAsync<JsonElement>("/api/inventory/expiring?withinDays=365");
        Assert.Empty(expiring.GetProperty("data").EnumerateArray());
        Assert.Empty((await mine.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/expiring?productId={theirProduct}")).GetProperty("data").EnumerateArray());
        var low = Assert.Single((await mine.Stock.GetFromJsonAsync<JsonElement>("/api/inventory/low-stock")).GetProperty("data").EnumerateArray());
        Assert.Equal((myProduct, 0m), (low.GetProperty("product").GetProperty("id").GetGuid(), Number(low, "onOrder")));
        var alerts = await mine.Stock.GetFromJsonAsync<JsonElement>("/api/inventory/alerts?withinDays=365");
        Assert.Equal((1, 0, 0), (alerts.GetProperty("lowStock").GetInt32(), alerts.GetProperty("expiredLots").GetInt32(), alerts.GetProperty("expiringLots").GetInt32()));
    }

    [Theory]
    [InlineData("/api/inventory/low-stock")]
    [InlineData("/api/inventory/expiring")]
    [InlineData("/api/inventory/alerts")]
    public async Task Alerts_NeedASignedInUser(string url)
    {
        await factory.MigrateAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(url)).StatusCode);
    }
}
