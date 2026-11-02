using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Stock valuation and the stock movement summary (B-24) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class InventoryReportsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private sealed record Clinic(HttpClient Admin, HttpClient Stock, HttpClient Desk, HttpClient Vet, Guid Patient);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var stock = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Inventory));
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        var vet = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian));
        await OkAsync(await admin.PatchAsJsonAsync("/api/clinic", new { currency = "EUR" }));
        var client = (await OkAsync(await desk.PostAsJsonAsync("/api/clients", new { firstName = "Jane", lastName = "Doe" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var patient = (await OkAsync(await desk.PostAsJsonAsync("/api/patients", new { clientId = client, name = "Rex", species = "Dog" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        return new Clinic(admin, stock, desk, vet, patient);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private DateTimeOffset Now => factory.Clock.GetUtcNow();

    private static string Day(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime).ToString("yyyy-MM-dd");

    private static async Task<Guid> ProductAsync(Clinic clinic, string name, string? category = null) =>
        (await OkAsync(await clinic.Stock.PostAsJsonAsync("/api/products", new { sku = name.ToUpperInvariant(), name, type = "Supply", unit = "Each", category }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

    private static async Task<JsonElement> MoveAsync(HttpClient http, object body) =>
        await OkAsync(await http.PostAsJsonAsync("/api/inventory/movements", body), HttpStatusCode.Created);

    /// <summary>Returns (lot id, movement id).</summary>
    private static async Task<(Guid Lot, Guid Movement)> ReceiveAsync(Clinic clinic, Guid productId, decimal quantity, decimal? unitCost, string lotNumber = "L1", DateTimeOffset? occurredAt = null)
    {
        var movement = await MoveAsync(clinic.Stock, new { type = "Receive", productId, quantity, unitCost, lotNumber, occurredAt });
        return (movement.GetProperty("lot").GetProperty("id").GetGuid(), movement.GetProperty("id").GetGuid());
    }

    private static decimal Number(JsonElement e, string p) => e.GetProperty(p).GetDecimal();

    private static int Int(JsonElement e, string p) => e.GetProperty(p).GetInt32();

    // ---------- Stock valuation ----------

    [Fact]
    public async Task Valuation_ValuesEachLotAtTheAverageCostOfItsStandingReceipts()
    {
        var clinic = await ClinicAsync();
        var gauze = await ProductAsync(clinic, "Gauze");
        var (lot, _) = await ReceiveAsync(clinic, gauze, 10, 2);
        await ReceiveAsync(clinic, gauze, 10, 3); // the same lot: 20 at 2.50 on average
        await MoveAsync(clinic.Stock, new { type = "Adjust", stockLotId = lot, quantity = -5, reason = "Count" });
        await ReceiveAsync(clinic, gauze, 4, null, "L2"); // uncosted
        var kibble = await ProductAsync(clinic, "Kibble", "Food");
        await ReceiveAsync(clinic, kibble, 3, 1.3333m);
        var tape = await ProductAsync(clinic, "Tape");
        var (_, mistake) = await ReceiveAsync(clinic, tape, 5, 10);
        await ReceiveAsync(clinic, tape, 3, 1);
        await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{mistake}/reverse", new { reason = "Wrong product" }), HttpStatusCode.Created);
        var (empty, _) = await ReceiveAsync(clinic, await ProductAsync(clinic, "Empty"), 2, 5);
        await MoveAsync(clinic.Stock, new { type = "Adjust", stockLotId = empty, quantity = -2, reason = "Count" }); // no stock: not listed

        var report = await OkAsync(await clinic.Stock.GetAsync("/api/reports/stock-valuation"));
        Assert.Equal(["asOf", "currency", "totals", "byCategory", "products"], report.EnumerateObject().Select(p => p.Name));
        var totals = report.GetProperty("totals");
        Assert.Equal((3, 4, 1, 44.5m), (Int(totals, "products"), Int(totals, "lots"), Int(totals, "uncostedLots"), Number(totals, "value")));

        var products = report.GetProperty("products").EnumerateArray().ToList();
        Assert.Equal(["Gauze", "Kibble", "Tape"], products.Select(p => p.GetProperty("product").GetProperty("name").GetString()));
        Assert.Equal(["product", "lots", "uncostedLots", "onHand", "costedQuantity", "uncostedQuantity", "value", "unitCost"], products[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal((2, 1, 19m, 15m, 4m, 37.5m, 2.5m), (Int(products[0], "lots"), Int(products[0], "uncostedLots"), Number(products[0], "onHand"),
            Number(products[0], "costedQuantity"), Number(products[0], "uncostedQuantity"), Number(products[0], "value"), Number(products[0], "unitCost")));
        Assert.Equal((4m, 1.3333m), (Number(products[1], "value"), Number(products[1], "unitCost"))); // 3.9999 rounded to cents
        Assert.Equal((3m, 1m), (Number(products[2], "value"), Number(products[2], "unitCost"))); // the reversed receipt does not count

        var categories = report.GetProperty("byCategory").EnumerateArray().ToList();
        Assert.Equal(["Food", null], categories.Select(c => c.GetProperty("category").GetString()));
        Assert.Equal((1, 4m), (Int(categories[0], "products"), Number(categories[0], "value")));
        Assert.Equal((2, 1, 40.5m), (Int(categories[1], "products"), Int(categories[1], "uncostedLots"), Number(categories[1], "value")));
    }

    // ---------- Movement summary ----------

    [Fact]
    public async Task Movements_SummariseEachTypeInThePeriod_BetweenOpeningAndClosingStock()
    {
        var clinic = await ClinicAsync();
        var start = Now.AddDays(-10);
        var gauze = await ProductAsync(clinic, "Gauze");
        var (lot, _) = await ReceiveAsync(clinic, gauze, 20, 1, occurredAt: Now.AddDays(-20)); // before the period
        await ReceiveAsync(clinic, gauze, 10, 1, occurredAt: start.AddHours(1));
        var dispensed = await MoveAsync(clinic.Vet, new { type = "Dispense", stockLotId = lot, quantity = 3, patientId = clinic.Patient });
        await MoveAsync(clinic.Vet, new { type = "Dispense", stockLotId = lot, quantity = 2, patientId = clinic.Patient });
        await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{dispensed.GetProperty("id").GetGuid()}/reverse", new { reason = "Not given" }), HttpStatusCode.Created);
        await MoveAsync(clinic.Stock, new { type = "Sell", stockLotId = lot, quantity = 4 });
        await MoveAsync(clinic.Stock, new { type = "Adjust", stockLotId = lot, quantity = -1, reason = "Count" });
        await MoveAsync(clinic.Stock, new { type = "ReturnToSupplier", stockLotId = lot, quantity = 6, reason = "Damaged" });
        await MoveAsync(clinic.Stock, new { type = "ReturnFromClient", stockLotId = lot, quantity = 1, reason = "Unopened" });
        var idle = await ProductAsync(clinic, "Idle");
        await ReceiveAsync(clinic, idle, 7, null, occurredAt: Now.AddDays(-30)); // stock held, nothing moved
        await ProductAsync(clinic, "Never"); // never stocked: not listed

        var report = await OkAsync(await clinic.Stock.GetAsync($"/api/reports/stock-movements?from={Day(start)}&to={Day(Now)}"));
        Assert.Equal(["from", "to", "timeZone", "products"], report.EnumerateObject().Select(p => p.Name));
        var rows = report.GetProperty("products").EnumerateArray().ToList();
        Assert.Equal(["Gauze", "Idle"], rows.Select(r => r.GetProperty("product").GetProperty("name").GetString()));
        Assert.Equal(
            ["product", "opening", "received", "returnedFromClient", "dispensed", "sold", "returnedToSupplier", "expired", "adjusted", "net", "closing"],
            rows[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal((20m, 10m, 1m, 2m, 4m, 6m, 0m, -1m, -2m, 18m),
            (Number(rows[0], "opening"), Number(rows[0], "received"), Number(rows[0], "returnedFromClient"), Number(rows[0], "dispensed"), Number(rows[0], "sold"),
                Number(rows[0], "returnedToSupplier"), Number(rows[0], "expired"), Number(rows[0], "adjusted"), Number(rows[0], "net"), Number(rows[0], "closing")));
        Assert.Equal("0", rows[0].GetProperty("expired").GetRawText());
        Assert.Equal((7m, 0m, 7m), (Number(rows[1], "opening"), Number(rows[1], "net"), Number(rows[1], "closing")));

        // The closing stock is the lot's quantity today.
        var latest = (await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/movements?stockLotId={lot}&pageSize=1")).GetProperty("data")[0];
        Assert.Equal(18m, Number(latest, "balanceAfter"));

        var before = await OkAsync(await clinic.Stock.GetAsync($"/api/reports/stock-movements?from={Day(Now.AddDays(-25))}&to={Day(Now.AddDays(-15))}"));
        var gauzeBefore = before.GetProperty("products").EnumerateArray().Single(r => r.GetProperty("product").GetProperty("id").GetGuid() == gauze);
        Assert.Equal((0m, 20m, 20m), (Number(gauzeBefore, "opening"), Number(gauzeBefore, "received"), Number(gauzeBefore, "closing")));
    }

    // ---------- Rules ----------

    [Fact]
    public async Task InventoryReports_AreForAdministratorsAndInventoryStaff_AndStayInTheClinic()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        await ReceiveAsync(other, await ProductAsync(other, "Gauze"), 5, 2);
        var movements = $"/api/reports/stock-movements?from={Day(Now.AddDays(-1))}&to={Day(Now)}";

        Assert.Equal(0, Int((await OkAsync(await clinic.Admin.GetAsync("/api/reports/stock-valuation"))).GetProperty("totals"), "products"));
        Assert.Equal(0, (await OkAsync(await clinic.Stock.GetAsync(movements))).GetProperty("products").GetArrayLength());
        Assert.Equal(10m, Number((await OkAsync(await other.Stock.GetAsync("/api/reports/stock-valuation"))).GetProperty("totals"), "value"));
        foreach (var url in new[] { "/api/reports/stock-valuation", movements })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Vet.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(url)).StatusCode);
        }

        // Inventory staff do not get the financial reports.
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Stock.GetAsync("/api/reports/receivables")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Stock.GetAsync("/api/reports/stock-movements?from=2026-01-01&to=2027-01-02")).StatusCode);
        Assert.Contains(Permissions.ReportsInventory, (await clinic.Stock.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }
}
