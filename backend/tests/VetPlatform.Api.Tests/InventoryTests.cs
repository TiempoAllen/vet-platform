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

/// <summary>Products, stock lots and stock receipts (B-19) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class InventoryTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private sealed record Clinic(HttpClient Admin, User AdminUser, HttpClient Stock, HttpClient Vet, HttpClient Nurse, HttpClient Desk, Guid Standard);

    private async Task<Clinic> ClinicAsync(string? currency = "EUR")
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var stock = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Inventory));
        var vet = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian));
        var nurse = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Nurse));
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        if (currency is not null)
        {
            await OkAsync(await admin.PatchAsJsonAsync("/api/clinic", new { currency }));
        }

        var standard = (await OkAsync(await admin.PostAsJsonAsync("/api/tax-rates", new { name = "Standard", ratePercent = 20 }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        return new Clinic(admin, adminUser, stock, vet, nurse, desk, standard);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> ProductAsync(Clinic clinic, string sku, string type = "Supply", string unit = "Each", string? name = null, string? category = null) =>
        (await OkAsync(
            await clinic.Stock.PostAsJsonAsync("/api/products", new { sku, name = name ?? sku, type, unit, category }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> ReceiveAsync(Clinic clinic, object body) =>
        clinic.Stock.PostAsJsonAsync("/api/inventory/movements", body);

    private string InAYear => DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime).AddYears(1).ToString("yyyy-MM-dd");

    private static decimal Number(JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    // ---------- Products ----------

    [Fact]
    public async Task Products_AreCreated_Read_AndUpdated()
    {
        var clinic = await ClinicAsync();
        var response = await clinic.Stock.PostAsJsonAsync("/api/products", new
        {
            sku = " AMX-250 ", name = " Amoxicillin 250 mg ", category = "Antibiotics", type = "Medication", unit = "Tablet",
            reorderLevel = 100.5m, defaultUnitPrice = 0.45m, defaultTaxRateId = clinic.Standard,
        });
        var created = await OkAsync(response, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal($"/api/products/{id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal(
            ["id", "sku", "name", "category", "type", "unit", "reorderLevel", "defaultUnitPrice", "defaultTaxRate", "status", "quantityOnHand", "createdAt", "updatedAt"],
            created.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("AMX-250", "Amoxicillin 250 mg", "Active", 0m), (created.GetProperty("sku").GetString(), created.GetProperty("name").GetString(), created.GetProperty("status").GetString(), Number(created, "quantityOnHand")));
        Assert.Equal(("Standard", 20m, "Active"), (created.GetProperty("defaultTaxRate").GetProperty("name").GetString(), Number(created.GetProperty("defaultTaxRate"), "percent"), created.GetProperty("defaultTaxRate").GetProperty("status").GetString()));
        Assert.Equal((100.5m, 0.45m), (Number(created, "reorderLevel"), Number(created, "defaultUnitPrice")));

        var updated = await OkAsync(await clinic.Stock.PatchAsJsonAsync($"/api/products/{id}", new { name = "Amoxicillin 250mg", category = "", reorderLevel = (decimal?)null, defaultTaxRateId = (Guid?)null, status = "Inactive" }));
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("reorderLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("defaultTaxRate").ValueKind);
        Assert.Equal(0.45m, Number(updated, "defaultUnitPrice")); // omitted: unchanged
        Assert.Equal("Inactive", updated.GetProperty("status").GetString());

        var read = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/products/{id}");
        Assert.Equal("Amoxicillin 250mg", read.GetProperty("name").GetString());

        var entries = await factory.AuditEntriesAsync(id);
        Assert.Equal([AuditActions.ProductCreated, AuditActions.ProductUpdated], entries.Select(e => e.Action));
        Assert.Contains("\"category\"", entries[1].Metadata);
        Assert.All(entries, e => Assert.DoesNotContain("Amoxicillin", e.Metadata ?? ""));
        Assert.All(entries, e => Assert.DoesNotContain("AMX", e.Metadata ?? ""));
    }

    [Fact]
    public async Task Services_HoldNoStock_AndHaveNoReorderLevel()
    {
        var clinic = await ClinicAsync();
        var service = await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/products", new { sku = "CONSULT", name = "Consultation", type = "Service", unit = "Each", defaultUnitPrice = 45 }), HttpStatusCode.Created);
        Assert.Equal(JsonValueKind.Null, service.GetProperty("quantityOnHand").ValueKind);

        var withLevel = await clinic.Stock.PostAsJsonAsync("/api/products", new { sku = "SURG", name = "Surgery", type = "Service", unit = "Each", reorderLevel = 1 });
        Assert.Equal("reorderLevel", Assert.Single((await ErrorOf(withLevel)).Details).Field);

        var supply = await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/products", new { sku = "GAUZE", name = "Gauze", type = "Supply", unit = "Pack", reorderLevel = 5 }), HttpStatusCode.Created);
        var toService = await clinic.Stock.PatchAsJsonAsync($"/api/products/{supply.GetProperty("id").GetGuid()}", new { type = "Service" });
        Assert.Equal("reorderLevel", Assert.Single((await ErrorOf(toService)).Details).Field);

        var received = await ReceiveAsync(clinic, new { type = "Receive", productId = service.GetProperty("id").GetGuid(), quantity = 1 });
        Assert.Equal("productId", Assert.Single((await ErrorOf(received)).Details).Field);
    }

    [Fact]
    public async Task InvalidProducts_AreRefused()
    {
        var clinic = await ClinicAsync();
        var archived = (await OkAsync(await clinic.Admin.PostAsJsonAsync("/api/tax-rates", new { name = "Old", ratePercent = 5 }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Admin.PatchAsJsonAsync($"/api/tax-rates/{archived}", new { status = "Archived" }));

        var cases = new (object Body, string Field)[]
        {
            (new { sku = "-AB", name = "x", type = "Supply", unit = "Each" }, "sku"),
            (new { sku = "A B", name = "x", type = "Supply", unit = "Each" }, "sku"),
            (new { sku = "A1", name = " ", type = "Supply", unit = "Each" }, "name"),
            (new { sku = "A1", name = "x", type = "Supply", unit = "Each", reorderLevel = -1 }, "reorderLevel"),
            (new { sku = "A1", name = "x", type = "Supply", unit = "Each", reorderLevel = 1.0001m }, "reorderLevel"),
            (new { sku = "A1", name = "x", type = "Supply", unit = "Each", defaultUnitPrice = 1.00001m }, "defaultUnitPrice"),
            (new { sku = "A1", name = "x", type = "Supply", unit = "Each", defaultTaxRateId = archived }, "defaultTaxRateId"),
            (new { sku = "A1", name = "x", type = "Supply", unit = "Each", defaultTaxRateId = Guid.NewGuid() }, "defaultTaxRateId"),
            (new { sku = new string('A', 51), name = "x", type = "Supply", unit = "Each" }, "Sku"),
        };
        foreach (var (body, field) in cases)
        {
            var response = await clinic.Stock.PostAsJsonAsync("/api/products", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(field, Assert.Single((await ErrorOf(response)).Details).Field, ignoreCase: true);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Stock.PostAsJsonAsync("/api/products", new { sku = "A1", name = "x", unit = "Each" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await clinic.Stock.PostAsJsonAsync("/api/products", new { sku = "A1", name = "x", type = "Food", unit = "Each" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await clinic.Stock.PostAsJsonAsync("/api/products", new { sku = "A1", name = "x", type = "Supply", unit = "tab" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Stock.GetAsync("/api/products?type=Food")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Stock.GetAsync("/api/products?status=active")).StatusCode);

        await ProductAsync(clinic, "Dup-1");
        var duplicate = await clinic.Stock.PostAsJsonAsync("/api/products", new { sku = "dup-1", name = "Other", type = "Retail", unit = "Each" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var other = await ProductAsync(clinic, "Dup-2");
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PatchAsJsonAsync($"/api/products/{other}", new { sku = "DUP-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Stock.PatchAsJsonAsync($"/api/products/{Guid.NewGuid()}", new { name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Stock.PatchAsJsonAsync($"/api/products/{Guid.NewGuid()}", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task ProductList_Searches_AndFilters()
    {
        var clinic = await ClinicAsync();
        var amox = await ProductAsync(clinic, "AMX-250", "Medication", "Tablet", "Amoxicillin", "Antibiotics");
        var rabies = await ProductAsync(clinic, "VAC-RAB", "Vaccine", "Dose", "Rabies vaccine", "vaccines");
        var collar = await ProductAsync(clinic, "COL_1", "Retail", "Each", "Collar 100%", "Retail");
        await OkAsync(await clinic.Stock.PatchAsJsonAsync($"/api/products/{collar}", new { status = "Inactive" }));

        async Task<List<Guid>> Ids(string query) =>
            (await clinic.Nurse.GetFromJsonAsync<JsonElement>("/api/products" + query)).GetProperty("data").EnumerateArray()
                .Select(p => p.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([amox, collar, rabies], await Ids(""));
        Assert.Equal([amox], await Ids("?q=amx")); // SKU, ignoring case
        Assert.Equal([rabies], await Ids("?q=RABIES"));
        Assert.Equal([collar], await Ids("?q=100%25"));
        Assert.Equal([collar], await Ids("?q=l_1"));
        Assert.Empty(await Ids("?q=_X"));
        Assert.Equal([rabies], await Ids("?type=Vaccine"));
        Assert.Equal([amox, rabies], await Ids("?status=Active"));
        Assert.Equal([rabies], await Ids("?category=VACCINES"));

        var categories = await clinic.Desk.GetFromJsonAsync<JsonElement>("/api/products/categories");
        Assert.Equal(["Antibiotics", "Retail", "vaccines"], categories.EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task Permissions_ReadWide_ManageNarrow()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "P-1");
        foreach (var reader in new[] { clinic.Admin, clinic.Stock, clinic.Vet, clinic.Nurse, clinic.Desk })
        {
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/products/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/inventory/stock")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/inventory/movements")).StatusCode);
        }

        foreach (var outsider in new[] { clinic.Vet, clinic.Nurse, clinic.Desk })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync("/api/products", new { sku = "X", name = "x", type = "Supply", unit = "Each" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PatchAsJsonAsync($"/api/products/{id}", new { name = "y" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync("/api/inventory/movements", new { type = "Receive", productId = id, quantity = 1 })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await clinic.Admin.PostAsJsonAsync("/api/inventory/movements", new { type = "Receive", productId = id, quantity = 1 })).StatusCode);
        var stockMe = await clinic.Stock.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal([Permissions.InventoryManage, Permissions.InventoryRead], stockMe.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Stock.GetAsync("/api/invoices")).StatusCode);
    }

    [Fact]
    public async Task OtherClinics_ProductsAndStock_AreInvisible()
    {
        var mine = await ClinicAsync();
        var theirs = await ClinicAsync();
        var theirProduct = await ProductAsync(theirs, "SHARED");
        await OkAsync(await ReceiveAsync(theirs, new { type = "Receive", productId = theirProduct, quantity = 5 }), HttpStatusCode.Created);
        await ProductAsync(mine, "SHARED"); // SKUs are unique per clinic only

        Assert.Equal(HttpStatusCode.NotFound, (await mine.Stock.GetAsync($"/api/products/{theirProduct}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mine.Stock.PatchAsJsonAsync($"/api/products/{theirProduct}", new { name = "x" })).StatusCode);
        var receive = await ReceiveAsync(mine, new { type = "Receive", productId = theirProduct, quantity = 1 });
        var unknown = await ReceiveAsync(mine, new { type = "Receive", productId = Guid.NewGuid(), quantity = 1 });
        Assert.Equal((await ErrorOf(unknown)).Details!.Single().Message, Assert.Single((await ErrorOf(receive)).Details).Message);
        Assert.Empty((await mine.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/stock?productId={theirProduct}")).GetProperty("data").EnumerateArray());
        Assert.Empty((await mine.Stock.GetFromJsonAsync<JsonElement>("/api/inventory/movements")).GetProperty("data").EnumerateArray());
        Assert.Single((await mine.Stock.GetFromJsonAsync<JsonElement>("/api/products")).GetProperty("data").EnumerateArray());
    }

    // ---------- Receiving stock ----------

    [Fact]
    public async Task Receiving_CreatesOrTopsUpTheMatchingLot()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "AMX", "Medication", "Tablet");
        var expires = InAYear;

        var first = await OkAsync(await ReceiveAsync(clinic, new
        {
            type = "Receive", productId = id, quantity = 100, lotNumber = " L-1 ", expiresOn = expires, location = "Pharmacy",
            unitCost = 0.1234m, reference = "DN-5531", notes = "Box slightly damaged",
        }), HttpStatusCode.Created);
        Assert.Equal(
            ["id", "type", "product", "lot", "quantity", "unitCost", "currency", "reference", "notes", "occurredAt", "performedBy", "createdAt"],
            first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("L-1", 100m, "EUR", 0.1234m), (first.GetProperty("lot").GetProperty("lotNumber").GetString(), Number(first.GetProperty("lot"), "quantity"), first.GetProperty("currency").GetString(), Number(first, "unitCost")));
        var lotId = first.GetProperty("lot").GetProperty("id").GetGuid();

        // Same lot (lot number and location ignore case): topped up. Another location: a new lot.
        var second = await OkAsync(await ReceiveAsync(clinic, new { type = "Receive", productId = id, quantity = 20.5m, lotNumber = "l-1", expiresOn = expires, location = "PHARMACY" }), HttpStatusCode.Created);
        Assert.Equal(lotId, second.GetProperty("lot").GetProperty("id").GetGuid());
        Assert.Equal(120.5m, Number(second.GetProperty("lot"), "quantity"));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("unitCost").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("currency").ValueKind);
        var third = await OkAsync(await ReceiveAsync(clinic, new { type = "Receive", productId = id, quantity = 10, lotNumber = "L-1", expiresOn = expires }), HttpStatusCode.Created);
        Assert.NotEqual(lotId, third.GetProperty("lot").GetProperty("id").GetGuid());

        var product = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/products/{id}");
        Assert.Equal(130.5m, Number(product, "quantityOnHand"));
        var stock = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/inventory/stock?productId={id}");
        var lots = stock.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(2, lots.Count);
        Assert.Equal(
            ["id", "product", "lotNumber", "expiresOn", "location", "quantity", "createdAt", "updatedAt"],
            lots[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal([120.5m, 10m], lots.Select(l => Number(l, "quantity")));

        var movements = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/inventory/movements?stockLotId={lotId}");
        Assert.Equal([20.5m, 100m], movements.GetProperty("data").EnumerateArray().Select(m => Number(m, "quantity")));
        Assert.Equal(3, (await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/inventory/movements?productId={id}&type=Receive")).GetProperty("pagination").GetProperty("total").GetInt32());

        var entry = (await factory.AuditEntriesAsync(first.GetProperty("id").GetGuid())).Single();
        Assert.Equal(AuditActions.StockReceived, entry.Action);
        Assert.Contains("\"newLot\": true", entry.Metadata);
        Assert.DoesNotContain("DN-5531", entry.Metadata);
        Assert.DoesNotContain("damaged", entry.Metadata);
        Assert.DoesNotContain("L-1", entry.Metadata);
        Assert.DoesNotContain("Pharmacy", entry.Metadata);
    }

    [Fact]
    public async Task InvalidReceipts_AreRefused()
    {
        var clinic = await ClinicAsync();
        var vaccine = await ProductAsync(clinic, "VAC", "Vaccine", "Dose");
        var supply = await ProductAsync(clinic, "SUP");
        var inactive = await ProductAsync(clinic, "OLD");
        await OkAsync(await clinic.Stock.PatchAsJsonAsync($"/api/products/{inactive}", new { status = "Inactive" }));
        var now = factory.Clock.GetUtcNow();
        var yesterday = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-2).ToString("yyyy-MM-dd");

        var cases = new (object Body, string Field)[]
        {
            (new { type = "Receive", productId = vaccine, quantity = 1 }, "expiresOn"),
            (new { type = "Receive", productId = supply, quantity = 1, expiresOn = yesterday }, "expiresOn"),
            (new { type = "Receive", productId = supply, quantity = 1, expiresOn = "2101-01-01" }, "expiresOn"),
            (new { type = "Receive", productId = supply, quantity = 0 }, "quantity"),
            (new { type = "Receive", productId = supply, quantity = 1.0001m }, "quantity"),
            (new { type = "Receive", productId = supply, quantity = 1_000_001 }, "quantity"),
            (new { type = "Receive", productId = supply, quantity = 1, unitCost = -1 }, "unitCost"),
            (new { type = "Receive", productId = supply, quantity = 1, unitCost = 0.00001m }, "unitCost"),
            (new { type = "Receive", productId = supply, quantity = 1, occurredAt = now.AddMinutes(5) }, "occurredAt"),
            (new { type = "Receive", productId = inactive, quantity = 1 }, "productId"),
            (new { type = "Receive", productId = supply, quantity = 1, lotNumber = new string('x', 51) }, "LotNumber"),
            (new { type = "Receive", productId = supply, quantity = 1, reference = new string('x', 101) }, "Reference"),
        };
        foreach (var (body, field) in cases)
        {
            var response = await ReceiveAsync(clinic, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(field, Assert.Single((await ErrorOf(response)).Details).Field, ignoreCase: true);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await ReceiveAsync(clinic, new { type = "Dispense", productId = supply, quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ReceiveAsync(clinic, new { productId = supply, quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Stock.GetAsync("/api/inventory/movements?type=Sell")).StatusCode);

        var noCurrency = await ClinicAsync(currency: null);
        var product = await ProductAsync(noCurrency, "SUP");
        var cost = await ReceiveAsync(noCurrency, new { type = "Receive", productId = product, quantity = 1, unitCost = 2 });
        Assert.Equal("unitCost", Assert.Single((await ErrorOf(cost)).Details).Field);
        Assert.Equal(HttpStatusCode.Created, (await ReceiveAsync(noCurrency, new { type = "Receive", productId = product, quantity = 1 })).StatusCode);
    }

    [Fact]
    public async Task EmptyLots_AreHiddenUnlessAsked_AndBackdatedReceiptsKeepTheirTime()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "SUP");
        var earlier = factory.Clock.GetUtcNow().AddDays(-3);
        var received = await OkAsync(await ReceiveAsync(clinic, new { type = "Receive", productId = id, quantity = 2, occurredAt = earlier }), HttpStatusCode.Created);
        Assert.Equal(earlier.ToUnixTimeMilliseconds(), received.GetProperty("occurredAt").GetDateTimeOffset().ToUnixTimeMilliseconds());

        // An empty lot (B-20 will dispense lots to 0), made here with the guard triggers switched off for the statement.
        await factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "BEGIN; SET LOCAL session_replication_role = replica; UPDATE stock_lots SET quantity = 0 WHERE product_id = {0}; COMMIT;", id));

        Assert.Empty((await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/stock?productId={id}")).GetProperty("data").EnumerateArray());
        Assert.Single((await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/stock?productId={id}&includeEmpty=true")).GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task TypeAndUnit_AreFixed_OnceStockIsReceived()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "SUP");
        await OkAsync(await clinic.Stock.PatchAsJsonAsync($"/api/products/{id}", new { type = "Retail", unit = "Box" }));
        await OkAsync(await ReceiveAsync(clinic, new { type = "Receive", productId = id, quantity = 3 }), HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PatchAsJsonAsync($"/api/products/{id}", new { unit = "Each" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PatchAsJsonAsync($"/api/products/{id}", new { type = "Supply" })).StatusCode);
        await OkAsync(await clinic.Stock.PatchAsJsonAsync($"/api/products/{id}", new { type = "Retail", unit = "Box", name = "Renamed" }));
    }

    [Fact]
    public async Task ConcurrentReceipts_OfANewLot_MakeOneLot()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "SUP");

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            ReceiveAsync(clinic, new { type = "Receive", productId = id, quantity = 2, lotNumber = "B7" })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var lot = Assert.Single((await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/stock?productId={id}")).GetProperty("data").EnumerateArray());
        Assert.Equal(10m, Number(lot, "quantity"));
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE stock_lots SET quantity = quantity + 1 WHERE product_id = {0}")]
    [InlineData("UPDATE stock_lots SET lot_number = 'other' WHERE product_id = {0}")]
    [InlineData("DELETE FROM stock_lots WHERE product_id = {0}")]
    [InlineData("UPDATE stock_movements SET quantity = quantity + 1 WHERE product_id = {0}")]
    [InlineData("DELETE FROM stock_movements WHERE product_id = {0}")]
    [InlineData("INSERT INTO stock_movements (id, clinic_id, product_id, stock_lot_id, type, quantity, occurred_at, performed_by_user_id, created_at, updated_at) SELECT gen_random_uuid(), clinic_id, product_id, stock_lot_id, 'Receive', 1, now(), performed_by_user_id, now(), now() FROM stock_movements WHERE product_id = {0}")]
    [InlineData("DELETE FROM products WHERE id = {0}")]
    [InlineData("UPDATE products SET unit = 'Box' WHERE id = {0}")]
    [InlineData("TRUNCATE stock_movements, stock_lots, products CASCADE")]
    public async Task Database_KeepsStockDerivedFromMovements(string sql)
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "SUP");
        await OkAsync(await ReceiveAsync(clinic, new { type = "Receive", productId = id, quantity = 3, lotNumber = "A" }), HttpStatusCode.Created);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal("42501", exception.SqlState);
    }

    [Theory]
    [InlineData("Service")]
    [InlineData("Medication")]
    public async Task Database_RefusesLotsTheProductCannotHold(string type)
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "P-" + type, type);

        // A Service lot, or a medication lot without an expiry date.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO stock_lots (id, clinic_id, product_id, quantity, created_at, updated_at) " +
            "SELECT gen_random_uuid(), clinic_id, id, 0, now(), now() FROM products WHERE id = {0}", id)));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Database_ReceivesStockOnlyIntoActiveProducts()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "SUP");
        await OkAsync(await ReceiveAsync(clinic, new { type = "Receive", productId = id, quantity = 3 }), HttpStatusCode.Created);
        await OkAsync(await clinic.Stock.PatchAsJsonAsync($"/api/products/{id}", new { status = "Inactive" }));

        // A balanced receipt (movement and lot together) into the now inactive product.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO stock_movements (id, clinic_id, product_id, stock_lot_id, type, quantity, occurred_at, performed_by_user_id, created_at, updated_at) " +
            "SELECT gen_random_uuid(), clinic_id, product_id, stock_lot_id, 'Receive', 1, now(), performed_by_user_id, now(), now() FROM stock_movements WHERE product_id = {0}; " +
            "UPDATE stock_lots SET quantity = quantity + 1 WHERE product_id = {0};", id)));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task TheClinicCurrency_IsFixed_OnceCostsOrPricesExist()
    {
        var priced = await ClinicAsync();
        await OkAsync(await priced.Stock.PostAsJsonAsync("/api/products", new { sku = "P", name = "Priced", type = "Retail", unit = "Each", defaultUnitPrice = 5 }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await priced.Admin.PatchAsJsonAsync("/api/clinic", new { currency = "USD" })).StatusCode);

        var costed = await ClinicAsync();
        var id = await ProductAsync(costed, "SUP");
        await OkAsync(await ReceiveAsync(costed, new { type = "Receive", productId = id, quantity = 1 }), HttpStatusCode.Created);
        await OkAsync(await costed.Admin.PatchAsJsonAsync("/api/clinic", new { currency = "USD" })); // no cost yet
        await OkAsync(await ReceiveAsync(costed, new { type = "Receive", productId = id, quantity = 1, unitCost = 2 }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await costed.Admin.PatchAsJsonAsync("/api/clinic", new { currency = "EUR" })).StatusCode);
    }

    [Fact]
    public async Task OtherClinics_TaxRatesAndLots_AreNotReachable()
    {
        var mine = await ClinicAsync();
        var theirs = await ClinicAsync();
        var theirProduct = await ProductAsync(theirs, "THEIRS");
        var theirLot = (await OkAsync(await ReceiveAsync(theirs, new { type = "Receive", productId = theirProduct, quantity = 1 }), HttpStatusCode.Created))
            .GetProperty("lot").GetProperty("id").GetGuid();

        var withTheirRate = await mine.Stock.PostAsJsonAsync("/api/products", new { sku = "A", name = "x", type = "Supply", unit = "Each", defaultTaxRateId = theirs.Standard });
        var withUnknownRate = await mine.Stock.PostAsJsonAsync("/api/products", new { sku = "A", name = "x", type = "Supply", unit = "Each", defaultTaxRateId = Guid.NewGuid() });
        Assert.Equal((await ErrorOf(withUnknownRate)).Details!.Single().Message, Assert.Single((await ErrorOf(withTheirRate)).Details).Message);
        var mineProduct = await ProductAsync(mine, "MINE");
        var patch = await mine.Stock.PatchAsJsonAsync($"/api/products/{mineProduct}", new { defaultTaxRateId = theirs.Standard });
        Assert.Equal("defaultTaxRateId", Assert.Single((await ErrorOf(patch)).Details).Field);

        Assert.Empty((await mine.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/movements?stockLotId={theirLot}")).GetProperty("data").EnumerateArray());
        Assert.Empty((await mine.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/stock?productId={theirProduct}&includeEmpty=true")).GetProperty("data").EnumerateArray());
    }

    // ---------- Invoice lines ----------

    [Fact]
    public async Task InvoiceLines_LinkActiveProductsOfTheClinic()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var product = await ProductAsync(clinic, "CONSULT", "Service");
        var inactive = await ProductAsync(clinic, "OLD");
        await OkAsync(await clinic.Stock.PatchAsJsonAsync($"/api/products/{inactive}", new { status = "Inactive" }));
        var theirs = await ProductAsync(other, "THEIRS");
        var client = await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/clients", new { firstName = "Jane", lastName = "Doe" }), HttpStatusCode.Created);
        var invoice = (await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/invoices", new { clientId = client.GetProperty("id").GetGuid() }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

        var linked = await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{invoice}/items", new { description = "Consultation", quantity = 1, unitPrice = 45, taxRateId = clinic.Standard, productId = product }));
        var line = linked.GetProperty("items")[0];
        Assert.Equal(product, line.GetProperty("productId").GetGuid());

        foreach (var bad in new[] { inactive, theirs, Guid.NewGuid() })
        {
            var response = await clinic.Desk.PostAsJsonAsync($"/api/invoices/{invoice}/items", new { description = "x", quantity = 1, unitPrice = 1, taxRateId = clinic.Standard, productId = bad });
            Assert.Equal("productId", Assert.Single((await ErrorOf(response)).Details).Field);
        }

        var lineId = line.GetProperty("id").GetGuid();
        var unlinked = await OkAsync(await clinic.Desk.PatchAsJsonAsync($"/api/invoices/{invoice}/items/{lineId}", new { productId = (Guid?)null }));
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("items")[0].GetProperty("productId").ValueKind);
        var relink = await clinic.Desk.PatchAsJsonAsync($"/api/invoices/{invoice}/items/{lineId}", new { productId = theirs });
        Assert.Equal("productId", Assert.Single((await ErrorOf(relink)).Details).Field);

        // Billing a product never changes stock.
        Assert.Empty((await clinic.Stock.GetFromJsonAsync<JsonElement>("/api/inventory/movements")).GetProperty("data").EnumerateArray());
    }
}
