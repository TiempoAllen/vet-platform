using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Outgoing stock movements, controlled drugs, reversals and clinical product links (B-20) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class StockMovementsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private sealed record Clinic(
        HttpClient Admin, User AdminUser, HttpClient Stock, User StockUser, HttpClient Vet, User VetUser, HttpClient Nurse, User NurseUser,
        HttpClient Desk, Guid Standard, Guid PatientId);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var stockUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Inventory);
        var vetUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian);
        var nurseUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Nurse);
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        await OkAsync(await admin.PatchAsJsonAsync("/api/clinic", new { currency = "EUR" }));
        var standard = (await OkAsync(await admin.PostAsJsonAsync("/api/tax-rates", new { name = "Standard", ratePercent = 20 }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var client = await OkAsync(await desk.PostAsJsonAsync("/api/clients", new { firstName = "Jane", lastName = "Doe" }), HttpStatusCode.Created);
        var patient = await OkAsync(await desk.PostAsJsonAsync("/api/patients", new { clientId = client.GetProperty("id").GetGuid(), name = "Rex", species = "Dog" }), HttpStatusCode.Created);
        return new Clinic(
            admin, adminUser, await factory.SignInAsync(stockUser), stockUser, await factory.SignInAsync(vetUser), vetUser,
            await factory.SignInAsync(nurseUser), nurseUser, desk, standard, patient.GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private string Today => DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd");

    private string InAYear => DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime).AddYears(1).ToString("yyyy-MM-dd");

    private static async Task<Guid> ProductAsync(Clinic clinic, string sku, string type = "Supply", bool controlled = false) =>
        (await OkAsync(
            await clinic.Stock.PostAsJsonAsync("/api/products", new { sku, name = sku, type, unit = "Each", controlled }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();

    /// <summary>Receives <paramref name="quantity"/> into a new lot of the product and returns the lot id.</summary>
    private async Task<Guid> LotAsync(Clinic clinic, Guid productId, decimal quantity, string lotNumber = "L1", string? expiresOn = null) =>
        (await OkAsync(
            await Move(clinic.Stock, new { type = "Receive", productId, quantity, lotNumber, expiresOn = expiresOn ?? InAYear }),
            HttpStatusCode.Created)).GetProperty("lot").GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> Move(HttpClient http, object body) => http.PostAsJsonAsync("/api/inventory/movements", body);

    private static decimal Number(JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    private static async Task<decimal> LotQuantityAsync(Clinic clinic, Guid productId) =>
        Number(Assert.Single((await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/stock?productId={productId}&includeEmpty=true"))
            .GetProperty("data").EnumerateArray()), "quantity");

    // ---------- Outgoing movements and the register ----------

    [Fact]
    public async Task EveryType_ChangesTheLot_AndTheRegisterShowsTheRunningBalance()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "SUP");
        var lot = await LotAsync(clinic, id, 10, expiresOn: Today);

        var steps = new (object Body, decimal Balance, string Action)[]
        {
            (new { type = "Sell", stockLotId = lot, quantity = 3 }, 7, AuditActions.StockSold),
            (new { type = "Adjust", stockLotId = lot, quantity = -2, reason = "Count" }, 5, AuditActions.StockAdjusted),
            (new { type = "Adjust", stockLotId = lot, quantity = 1, reason = "Found one" }, 6, AuditActions.StockAdjusted),
            (new { type = "ReturnToSupplier", stockLotId = lot, quantity = 1, reason = "Damaged box" }, 5, AuditActions.StockReturnedToSupplier),
            (new { type = "ReturnFromClient", stockLotId = lot, quantity = 1, reason = "Unopened" }, 6, AuditActions.StockReturnedFromClient),
            (new { type = "Dispense", stockLotId = lot, quantity = 2, patientId = clinic.PatientId }, 4, AuditActions.StockDispensed),
            (new { type = "Expire", stockLotId = lot, quantity = 4, reason = "Expired today" }, 0, AuditActions.StockExpired),
        };
        foreach (var (body, balance, action) in steps)
        {
            var movement = await OkAsync(await Move(clinic.Stock, body), HttpStatusCode.Created);
            Assert.Equal(balance, Number(movement, "balanceAfter"));
            var entry = Assert.Single(await factory.AuditEntriesAsync(movement.GetProperty("id").GetGuid()));
            Assert.Equal(action, entry.Action);
            Assert.DoesNotContain("Count", entry.Metadata);
            Assert.DoesNotContain("Unopened", entry.Metadata);
        }

        var register = await clinic.Nurse.GetFromJsonAsync<JsonElement>($"/api/products/{id}/register");
        Assert.Equal([10m, 7m, 5m, 6m, 5m, 6m, 4m, 0m], register.GetProperty("data").EnumerateArray().Select(m => Number(m, "balanceAfter")));
        Assert.Equal([10m, -3m, -2m, 1m, -1m, 1m, -2m, -4m], register.GetProperty("data").EnumerateArray().Select(m => Number(m, "quantity")));
        Assert.Equal(0m, await LotQuantityAsync(clinic, id));
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Nurse.GetAsync($"/api/products/{Guid.NewGuid()}/register")).StatusCode);
    }

    [Fact]
    public async Task Stock_NeverGoesBelowZero_EvenWithConcurrentSales()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "SUP");
        var lot = await LotAsync(clinic, id, 10);

        var tooMuch = await Move(clinic.Stock, new { type = "Sell", stockLotId = lot, quantity = 11 });
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Move(clinic.Stock, new { type = "Sell", stockLotId = lot, quantity = 3 })));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1m, await LotQuantityAsync(clinic, id));
    }

    [Fact]
    public async Task InvalidMovements_AreRefused()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "SUP");
        var other = await ProductAsync(clinic, "OTHER");
        var lot = await LotAsync(clinic, id, 5);

        var cases = new (object Body, string Field)[]
        {
            (new { type = "Sell", quantity = 1 }, "stockLotId"),
            (new { type = "Sell", stockLotId = Guid.NewGuid(), quantity = 1 }, "stockLotId"),
            (new { type = "Sell", stockLotId = lot, productId = other, quantity = 1 }, "stockLotId"),
            (new { type = "Sell", stockLotId = lot, quantity = -1 }, "quantity"),
            (new { type = "Adjust", stockLotId = lot, quantity = 0, reason = "x" }, "quantity"),
            (new { type = "Adjust", stockLotId = lot, quantity = 1 }, "reason"),
            (new { type = "ReturnToSupplier", stockLotId = lot, quantity = 1, reason = " " }, "reason"),
            (new { type = "Expire", stockLotId = lot, quantity = 1, reason = "x" }, "stockLotId"), // not expired yet
            (new { type = "Sell", stockLotId = lot, quantity = 1, lotNumber = "L1" }, "lotNumber"),
            (new { type = "Sell", stockLotId = lot, quantity = 1, unitCost = 1 }, "unitCost"),
            (new { type = "Sell", stockLotId = lot, quantity = 1, patientId = clinic.PatientId }, "patientId"),
            (new { type = "Dispense", stockLotId = lot, quantity = 1 }, "patientId"),
            (new { type = "Dispense", stockLotId = lot, quantity = 1, patientId = Guid.NewGuid() }, "patientId"),
            (new { type = "Dispense", stockLotId = lot, quantity = 1, patientId = clinic.PatientId, consultationId = Guid.NewGuid() }, "consultationId"),
            (new { type = "Adjust", stockLotId = lot, quantity = 1, reason = "x", invoiceItemId = Guid.NewGuid() }, "invoiceItemId"),
            (new { type = "Reversal", stockLotId = lot, quantity = 1, reason = "x" }, "type"),
            (new { type = "Receive", stockLotId = lot, productId = id, quantity = 1 }, "stockLotId"),
            (new { type = "Receive", productId = id, quantity = 1, witnessUserId = clinic.VetUser.Id }, "witnessUserId"),
            (new { type = "Adjust", stockLotId = lot, quantity = -1, reason = "x", witnessUserId = clinic.StockUser.Id }, "witnessUserId"), // self
            (new { type = "Adjust", stockLotId = lot, quantity = -1, reason = "x", witnessUserId = Guid.NewGuid() }, "witnessUserId"),
        };
        foreach (var (body, field) in cases)
        {
            var response = await Move(clinic.Stock, body);
            Assert.True(HttpStatusCode.UnprocessableEntity == response.StatusCode, $"{field}: {(int)response.StatusCode}");
            Assert.Contains(field, (await ErrorOf(response)).Details!.Select(d => d.Field));
        }

        Assert.Equal(5m, await LotQuantityAsync(clinic, id));
    }

    [Fact]
    public async Task Permissions_DispenseForClinicians_EverythingElseForInventory()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "SUP");
        var lot = await LotAsync(clinic, id, 20);
        var dispense = new { type = "Dispense", stockLotId = lot, quantity = 1, patientId = clinic.PatientId };

        foreach (var http in new[] { clinic.Vet, clinic.Nurse, clinic.Stock, clinic.Admin })
        {
            Assert.Equal(HttpStatusCode.Created, (await Move(http, dispense)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await Move(clinic.Desk, dispense)).StatusCode);
        foreach (var http in new[] { clinic.Vet, clinic.Nurse })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Move(http, new { type = "Sell", stockLotId = lot, quantity = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Move(http, new { type = "Receive", productId = id, quantity = 1 })).StatusCode);
        }

        var vetMe = await clinic.Vet.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Contains(Permissions.InventoryDispenseControlled, vetMe.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    // ---------- Controlled drugs ----------

    [Fact]
    public async Task ControlledDrugs_NeedVetsReasonsAndWitnesses()
    {
        var clinic = await ClinicAsync();
        var supply = await clinic.Stock.PostAsJsonAsync("/api/products", new { sku = "S", name = "S", type = "Supply", unit = "Each", controlled = true });
        Assert.Equal("controlled", Assert.Single((await ErrorOf(supply)).Details).Field);

        var id = await ProductAsync(clinic, "KET", "Medication", controlled: true);
        var lot = await LotAsync(clinic, id, 10);
        var listed = await clinic.Vet.GetFromJsonAsync<JsonElement>("/api/products?controlled=true");
        Assert.Equal([id], listed.GetProperty("data").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PatchAsJsonAsync($"/api/products/{id}", new { controlled = false })).StatusCode);

        var dispense = new { type = "Dispense", stockLotId = lot, quantity = 1, patientId = clinic.PatientId, reason = "Sedation for surgery" };
        Assert.Equal(HttpStatusCode.Forbidden, (await Move(clinic.Nurse, dispense)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Move(clinic.Stock, dispense)).StatusCode); // inventory.manage is not enough
        var noReason = await Move(clinic.Vet, new { type = "Dispense", stockLotId = lot, quantity = 1, patientId = clinic.PatientId });
        Assert.Equal("reason", Assert.Single((await ErrorOf(noReason)).Details).Field);
        await OkAsync(await Move(clinic.Vet, dispense), HttpStatusCode.Created);

        var unwitnessed = await Move(clinic.Stock, new { type = "Adjust", stockLotId = lot, quantity = -1, reason = "Broken vial" });
        Assert.Equal("witnessUserId", Assert.Single((await ErrorOf(unwitnessed)).Details).Field);
        var witnessed = await OkAsync(await Move(clinic.Stock, new { type = "Adjust", stockLotId = lot, quantity = -1, reason = "Broken vial", witnessUserId = clinic.VetUser.Id }), HttpStatusCode.Created);
        Assert.Equal(clinic.VetUser.Id, witnessed.GetProperty("witnessedBy").GetProperty("id").GetGuid());

        var sale = await Move(clinic.Stock, new { type = "Sell", stockLotId = lot, quantity = 1 });
        Assert.Equal("reason", Assert.Single((await ErrorOf(sale)).Details).Field);

        var register = await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/products/{id}/register");
        Assert.Equal([10m, 9m, 8m], register.GetProperty("data").EnumerateArray().Select(m => Number(m, "balanceAfter")));
    }

    // ---------- Sales and invoices ----------

    [Fact]
    public async Task Sales_MayNameALineOfAnIssuedInvoice_ForTheSameProduct()
    {
        var clinic = await ClinicAsync();
        var collar = await ProductAsync(clinic, "COLLAR", "Retail");
        var other = await ProductAsync(clinic, "LEASH", "Retail");
        var lot = await LotAsync(clinic, collar, 5);
        var client = await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/clients", new { firstName = "Al", lastName = "Lee" }), HttpStatusCode.Created);
        var invoice = (await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/invoices", new { clientId = client.GetProperty("id").GetGuid() }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var draft = await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{invoice}/items", new { description = "Collar", quantity = 1, unitPrice = 10, taxRateId = clinic.Standard, productId = collar }));
        var line = draft.GetProperty("items")[0].GetProperty("id").GetGuid();
        var otherLine = (await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{invoice}/items", new { description = "Leash", quantity = 1, unitPrice = 5, taxRateId = clinic.Standard, productId = other })))
            .GetProperty("items")[1].GetProperty("id").GetGuid();

        var onDraft = await Move(clinic.Stock, new { type = "Sell", stockLotId = lot, quantity = 1, invoiceItemId = line });
        Assert.Equal("invoiceItemId", Assert.Single((await ErrorOf(onDraft)).Details).Field);
        await OkAsync(await clinic.Desk.PostAsync($"/api/invoices/{invoice}/issue", null));
        var wrongProduct = await Move(clinic.Stock, new { type = "Sell", stockLotId = lot, quantity = 1, invoiceItemId = otherLine });
        Assert.Equal("invoiceItemId", Assert.Single((await ErrorOf(wrongProduct)).Details).Field);

        var sold = await OkAsync(await Move(clinic.Stock, new { type = "Sell", stockLotId = lot, quantity = 1, invoiceItemId = line }), HttpStatusCode.Created);
        Assert.Equal(line, sold.GetProperty("invoiceItemId").GetGuid());
    }

    // ---------- Reversals ----------

    [Fact]
    public async Task Reversals_UndoAMovementOnce_AndNeverGoBelowZero()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "SUP");
        var lot = await LotAsync(clinic, id, 5);
        var sale = (await OkAsync(await Move(clinic.Stock, new { type = "Sell", stockLotId = lot, quantity = 4 }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Vet.PostAsJsonAsync($"/api/inventory/movements/{sale}/reverse", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{sale}/reverse", new { })).StatusCode);
        var reversal = await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{sale}/reverse", new { reason = "Wrong lot" }), HttpStatusCode.Created);
        Assert.Equal(("Reversal", 4m, 5m, sale), (reversal.GetProperty("type").GetString(), Number(reversal, "quantity"), Number(reversal, "balanceAfter"), reversal.GetProperty("reversesMovementId").GetGuid()));
        Assert.Equal(AuditActions.StockReversed, Assert.Single(await factory.AuditEntriesAsync(reversal.GetProperty("id").GetGuid())).Action);

        var movements = await clinic.Stock.GetFromJsonAsync<JsonElement>($"/api/inventory/movements?stockLotId={lot}");
        var original = movements.GetProperty("data").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == sale);
        Assert.Equal(reversal.GetProperty("id").GetGuid(), original.GetProperty("reversedByMovementId").GetGuid());

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{sale}/reverse", new { reason = "again" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{reversal.GetProperty("id").GetGuid()}/reverse", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{Guid.NewGuid()}/reverse", new { reason = "x" })).StatusCode);

        // Reversing the receipt would take 5 out of a lot that now holds 5 - 3: refused.
        await OkAsync(await Move(clinic.Stock, new { type = "Sell", stockLotId = lot, quantity = 3 }), HttpStatusCode.Created);
        var receipt = movements.GetProperty("data").EnumerateArray().Single(m => m.GetProperty("type").GetString() == "Receive").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{receipt}/reverse", new { reason = "x" })).StatusCode);
        Assert.Equal(2m, await LotQuantityAsync(clinic, id));
    }

    [Fact]
    public async Task ControlledReversals_NeedAWitness()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "KET", "Medication", controlled: true);
        var lot = await LotAsync(clinic, id, 5);
        var dose = (await OkAsync(await Move(clinic.Vet, new { type = "Dispense", stockLotId = lot, quantity = 1, patientId = clinic.PatientId, reason = "Sedation" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

        var alone = await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{dose}/reverse", new { reason = "Not given" });
        Assert.Equal("witnessUserId", Assert.Single((await ErrorOf(alone)).Details).Field);
        await OkAsync(await clinic.Stock.PostAsJsonAsync($"/api/inventory/movements/{dose}/reverse", new { reason = "Not given", witnessUserId = clinic.NurseUser.Id }), HttpStatusCode.Created);
        Assert.Equal(5m, await LotQuantityAsync(clinic, id));
    }

    // ---------- Clinical product links ----------

    [Fact]
    public async Task Vaccinations_AndPrescriptions_LinkProductsOfTheirType()
    {
        var clinic = await ClinicAsync();
        var vaccine = await ProductAsync(clinic, "RAB", "Vaccine");
        var medication = await ProductAsync(clinic, "AMX", "Medication");
        var dose = new { vaccineName = "Rabies", administeredOn = Today, veterinarianId = clinic.VetUser.Id, productId = vaccine };
        var recorded = await OkAsync(await clinic.Nurse.PostAsJsonAsync($"/api/patients/{clinic.PatientId}/vaccinations", dose), HttpStatusCode.Created);
        Assert.Equal(vaccine, recorded.GetProperty("productId").GetGuid());
        var wrongType = await clinic.Nurse.PostAsJsonAsync($"/api/patients/{clinic.PatientId}/vaccinations", dose with { productId = medication });
        Assert.Equal("productId", Assert.Single((await ErrorOf(wrongType)).Details).Field);

        var consultation = (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var prescription = new { medicationName = "Amoxicillin", dose = "1 tablet", frequency = "Twice daily", quantity = 14, quantityUnit = "tablets", productId = medication };
        var added = await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{consultation}/prescriptions", prescription));
        var line = added.GetProperty("prescriptions")[0];
        Assert.Equal(medication, line.GetProperty("productId").GetGuid());
        var vaccineAsMedicine = await clinic.Vet.PostAsJsonAsync($"/api/consultations/{consultation}/prescriptions", prescription with { productId = vaccine });
        Assert.Equal("productId", Assert.Single((await ErrorOf(vaccineAsMedicine)).Details).Field);
        var unlinked = await OkAsync(await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{consultation}/prescriptions/{line.GetProperty("id").GetGuid()}", new { productId = (Guid?)null }));
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("prescriptions")[0].GetProperty("productId").ValueKind);

        // Linking never changes stock, and a dispense can name the consultation.
        Assert.Empty((await clinic.Stock.GetFromJsonAsync<JsonElement>("/api/inventory/movements")).GetProperty("data").EnumerateArray());
        var lot = await LotAsync(clinic, medication, 14);
        var dispensed = await OkAsync(await Move(clinic.Nurse, new { type = "Dispense", stockLotId = lot, quantity = 14, patientId = clinic.PatientId, consultationId = consultation }), HttpStatusCode.Created);
        Assert.Equal(consultation, dispensed.GetProperty("consultationId").GetGuid());
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE stock_lots SET quantity = quantity - 1 WHERE id = {0}; INSERT INTO stock_movements (id, clinic_id, product_id, stock_lot_id, type, quantity, balance_after, reason, occurred_at, performed_by_user_id, created_at, updated_at) SELECT gen_random_uuid(), clinic_id, product_id, stock_lot_id, 'Adjust', -1, 99, 'x', now(), performed_by_user_id, now(), now() FROM stock_movements WHERE stock_lot_id = {0} LIMIT 1")]
    [InlineData("UPDATE stock_lots SET quantity = 0 WHERE id = {0}; INSERT INTO stock_movements (id, clinic_id, product_id, stock_lot_id, type, quantity, balance_after, reason, occurred_at, performed_by_user_id, created_at, updated_at) SELECT gen_random_uuid(), clinic_id, product_id, stock_lot_id, 'Adjust', -6, -1, 'x', now(), performed_by_user_id, now(), now() FROM stock_movements WHERE stock_lot_id = {0} LIMIT 1")]
    [InlineData("UPDATE stock_lots SET quantity = quantity - 1 WHERE id = {0}; INSERT INTO stock_movements (id, clinic_id, product_id, stock_lot_id, type, quantity, balance_after, reason, reverses_movement_id, occurred_at, performed_by_user_id, created_at, updated_at) SELECT gen_random_uuid(), clinic_id, product_id, stock_lot_id, 'Reversal', -1, 4, 'x', id, now(), performed_by_user_id, now(), now() FROM stock_movements WHERE stock_lot_id = {0} LIMIT 1")]
    public async Task Database_KeepsTheRegisterExact(string sql)
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "SUP");
        var lot = await LotAsync(clinic, id, 5);

        await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, lot)));
        Assert.Equal(5m, await LotQuantityAsync(clinic, id));
    }

    [Fact]
    public async Task Database_EnforcesTheControlledDrugRules()
    {
        var clinic = await ClinicAsync();
        var id = await ProductAsync(clinic, "KET", "Medication", controlled: true);
        var lot = await LotAsync(clinic, id, 5);

        // A balanced, witness-less adjustment of a controlled drug.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE stock_lots SET quantity = 4 WHERE id = {0}; " +
            "INSERT INTO stock_movements (id, clinic_id, product_id, stock_lot_id, type, quantity, balance_after, reason, occurred_at, performed_by_user_id, created_at, updated_at) " +
            "SELECT gen_random_uuid(), clinic_id, product_id, stock_lot_id, 'Adjust', -1, 4, 'x', now(), performed_by_user_id, now(), now() FROM stock_movements WHERE stock_lot_id = {0} LIMIT 1", lot)));
        Assert.Equal("42501", exception.SqlState);

        var unflag = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE products SET controlled = false WHERE id = {0}", id)));
        Assert.Equal("42501", unflag.SqlState);
    }
}
