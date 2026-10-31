using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Tax rates, invoices and their lines (B-16) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class InvoicesTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed record Clinic(
        HttpClient Admin, User AdminUser, HttpClient Vet, User VetUser, HttpClient Nurse, HttpClient Desk,
        Guid ClientId, Guid PatientId, Guid Standard, Guid Zero);

    private async Task<Clinic> ClinicAsync(string currency = "EUR")
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var vetUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian);
        var nurse = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Nurse));
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        await OkAsync(await admin.PatchAsJsonAsync("/api/clinic", new { currency }));
        var standard = (await OkAsync(await admin.PostAsJsonAsync("/api/tax-rates", new { name = "Standard", ratePercent = 20 }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var zero = (await OkAsync(await admin.PostAsJsonAsync("/api/tax-rates", new { name = "Zero", ratePercent = 0 }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var clientId = await ClientAsync(desk);
        var patientId = await PatientAsync(desk, clientId, "Rex");
        return new Clinic(admin, adminUser, await factory.SignInAsync(vetUser), vetUser, nurse, desk, clientId, patientId, standard, zero);
    }

    private static async Task<Guid> ClientAsync(HttpClient desk)
    {
        var client = await desk.PostAsJsonAsync("/api/clients", new { firstName = "Jane", lastName = "Doe" });
        return (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PatientAsync(HttpClient desk, Guid clientId, string name)
    {
        var patient = await desk.PostAsJsonAsync("/api/patients", new { clientId, name, species = "Dog" });
        return (await patient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> DraftAsync(Clinic clinic, bool withPatient = true) =>
        (await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/invoices", new
        {
            clientId = clinic.ClientId, patientId = withPatient ? clinic.PatientId : (Guid?)null,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<JsonElement> AddLineAsync(Clinic clinic, Guid invoice, object line) =>
        await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{invoice}/items", line));

    private static object Exam(Clinic clinic) => new { description = "Consultation", quantity = 1, unitPrice = 45, taxRateId = clinic.Standard };

    private static async Task<JsonElement> IssueAsync(Clinic clinic, Guid invoice) =>
        await OkAsync(await clinic.Desk.PostAsync($"/api/invoices/{invoice}/issue", null));

    private static decimal Amount(JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    private DateOnly Today => DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);

    // ---------- Drafts, lines and totals ----------

    [Fact]
    public async Task AReceptionist_BuildsADraft_AndTheServerComputesEveryAmount()
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Desk.PostAsync("/api/invoices", Json($$"""
            { "clientId": "{{clinic.ClientId}}", "patientId": "{{clinic.PatientId}}", "notes": " Thank you ", "total": 1, "subtotal": 999 }
            """));
        var draft = await OkAsync(response, HttpStatusCode.Created);
        var id = draft.GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/invoices/{id}", response.Headers.Location!.ToString());
        Assert.Equal(
            ["id", "number", "status", "client", "patient", "billTo", "currency", "notes", "items", "subtotal", "discountTotal", "taxTotal",
             "total", "amountPaid", "balanceDue", "paymentStatus", "issuedAt", "dueOn", "issuedBy", "voidedAt", "voidedBy", "voidReason", "createdBy", "createdAt", "updatedAt"],
            draft.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("number").ValueKind);
        Assert.Equal("EUR", draft.GetProperty("currency").GetString());
        Assert.Equal(0m, Amount(draft, "total")); // client-supplied totals do not exist

        await AddLineAsync(clinic, id, new
        {
            description = "Vaccination", quantity = 3, unitPrice = 3.335m, discountType = "Percent", discountValue = 10, taxRateId = clinic.Standard,
        });
        var invoice = await AddLineAsync(clinic, id, new { description = "Pet food", quantity = 2, unitPrice = 12.5m, discountType = "Amount", discountValue = 5, taxRateId = clinic.Zero });

        var lines = invoice.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            ["id", "position", "description", "quantity", "unitPrice", "discountType", "discountValue", "taxRate", "consultationId", "productId",
             "net", "discount", "tax", "total"],
            lines[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal([1, 2], lines.Select(l => l.GetProperty("position").GetInt32()));
        Assert.Equal((10.01m, 1.00m, 1.80m, 10.81m), (Amount(lines[0], "net"), Amount(lines[0], "discount"), Amount(lines[0], "tax"), Amount(lines[0], "total")));
        Assert.Equal((25m, 5m, 0m, 20m), (Amount(lines[1], "net"), Amount(lines[1], "discount"), Amount(lines[1], "tax"), Amount(lines[1], "total")));
        Assert.Equal("Standard", lines[0].GetProperty("taxRate").GetProperty("name").GetString());
        Assert.Equal((35.01m, 6.00m, 1.80m, 30.81m), (Amount(invoice, "subtotal"), Amount(invoice, "discountTotal"), Amount(invoice, "taxTotal"), Amount(invoice, "total")));

        var firstLine = lines[0].GetProperty("id").GetGuid();
        var edited = await OkAsync(await clinic.Desk.PatchAsJsonAsync($"/api/invoices/{id}/items/{firstLine}", new { discountType = "None" }));
        Assert.Equal(12.01m, Amount(edited.GetProperty("items")[0], "total"));
        var removed = await OkAsync(await clinic.Desk.DeleteAsync($"/api/invoices/{id}/items/{lines[1].GetProperty("id").GetGuid()}"));
        Assert.Equal(12.01m, Amount(removed, "total"));

        var read = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}");
        Assert.Equal(removed.GetRawText(), read.GetRawText());
        Assert.All(await factory.AuditEntriesAsync(id), e => Assert.DoesNotContain("Vaccination", e.Metadata));
    }

    [Fact]
    public async Task Issuing_AssignsGaplessNumbers_AndFreezesTheInvoice()
    {
        var clinic = await ClinicAsync();
        var first = await DraftAsync(clinic);
        await AddLineAsync(clinic, first, Exam(clinic));
        var discarded = await DraftAsync(clinic); // a deleted draft must not leave a gap
        Assert.Equal(HttpStatusCode.NoContent, (await clinic.Desk.DeleteAsync($"/api/invoices/{discarded}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.GetAsync($"/api/invoices/{discarded}")).StatusCode);
        var second = await DraftAsync(clinic);
        await AddLineAsync(clinic, second, Exam(clinic));

        var issued = await IssueAsync(clinic, first);
        var issuedSecond = await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{second}/issue", new { dueOn = Today.AddDays(30).ToString("yyyy-MM-dd") }));

        Assert.Equal(("Issued", 1), (issued.GetProperty("status").GetString(), issued.GetProperty("number").GetInt32()));
        Assert.Equal(Today.ToString("yyyy-MM-dd"), issued.GetProperty("dueOn").GetString());
        Assert.Equal(2, issuedSecond.GetProperty("number").GetInt32());
        Assert.Equal(Today.AddDays(30).ToString("yyyy-MM-dd"), issuedSecond.GetProperty("dueOn").GetString());

        var line = issued.GetProperty("items")[0].GetProperty("id").GetGuid();
        var frozen = new[]
        {
            await clinic.Desk.PatchAsJsonAsync($"/api/invoices/{first}", new { notes = "x" }),
            await clinic.Desk.PostAsJsonAsync($"/api/invoices/{first}/items", Exam(clinic)),
            await clinic.Desk.PatchAsJsonAsync($"/api/invoices/{first}/items/{line}", new { quantity = 2 }),
            await clinic.Desk.DeleteAsync($"/api/invoices/{first}/items/{line}"),
            await clinic.Desk.DeleteAsync($"/api/invoices/{first}"),
            await clinic.Desk.PostAsync($"/api/invoices/{first}/issue", null),
        };
        Assert.All(frozen, r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Contains(AuditActions.InvoiceIssued, (await factory.AuditEntriesAsync(first)).Select(e => e.Action));
    }

    [Fact]
    public async Task ConcurrentIssues_GetDistinctConsecutiveNumbers()
    {
        var clinic = await ClinicAsync();
        var drafts = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            var draft = await DraftAsync(clinic);
            await AddLineAsync(clinic, draft, Exam(clinic));
            drafts.Add(draft);
        }

        var responses = await Task.WhenAll(drafts.Select(d => clinic.Desk.PostAsync($"/api/invoices/{d}/issue", null)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var numbers = new List<int>();
        foreach (var response in responses)
        {
            numbers.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("number").GetInt32());
        }

        Assert.Equal(Enumerable.Range(1, 8), numbers.Order());
    }

    [Fact]
    public async Task IssuingRules_AndVoiding()
    {
        var clinic = await ClinicAsync();
        var empty = await DraftAsync(clinic);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Desk.PostAsync($"/api/invoices/{empty}/issue", null)).StatusCode);
        await AddLineAsync(clinic, empty, Exam(clinic));
        var early = await clinic.Desk.PostAsJsonAsync($"/api/invoices/{empty}/issue", new { dueOn = Today.AddDays(-1).ToString("yyyy-MM-dd") });
        Assert.Equal("dueOn", Assert.Single((await ErrorOf(early)).Details).Field);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Desk.PostAsJsonAsync($"/api/invoices/{empty}/void", new { reason = "x" })).StatusCode);
        await IssueAsync(clinic, empty);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Desk.PostAsJsonAsync($"/api/invoices/{empty}/void", new { reason = " " })).StatusCode);
        var voided = await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{empty}/void", new { reason = " Wrong client " }));

        Assert.Equal("Void", voided.GetProperty("status").GetString());
        Assert.Equal("Wrong client", voided.GetProperty("voidReason").GetString());
        Assert.Equal(1, voided.GetProperty("number").GetInt32()); // the number stays taken
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Desk.PostAsJsonAsync($"/api/invoices/{empty}/void", new { reason = "again" })).StatusCode);
        Assert.DoesNotContain("Wrong client", (await factory.AuditEntriesAsync(empty)).Single(e => e.Action == AuditActions.InvoiceVoided).Metadata);
    }

    [Theory]
    [InlineData("""{ "description": "x", "quantity": 0, "unitPrice": 1 }""", "quantity")]
    [InlineData("""{ "description": "x", "quantity": 1.2345, "unitPrice": 1 }""", "quantity")]
    [InlineData("""{ "description": "x", "quantity": 1, "unitPrice": -1 }""", "unitPrice")]
    [InlineData("""{ "description": "x", "quantity": 1, "unitPrice": 1.23456 }""", "unitPrice")]
    [InlineData("""{ "description": "x", "quantity": 1, "unitPrice": 10, "discountType": "Percent", "discountValue": 0 }""", "discountValue")]
    [InlineData("""{ "description": "x", "quantity": 1, "unitPrice": 10, "discountType": "Percent", "discountValue": 101 }""", "discountValue")]
    [InlineData("""{ "description": "x", "quantity": 1, "unitPrice": 10, "discountType": "Amount", "discountValue": 10.01 }""", "discountValue")]
    [InlineData("""{ "description": "x", "quantity": 1, "unitPrice": 10, "discountType": "Amount", "discountValue": 1.001 }""", "discountValue")]
    [InlineData("""{ "description": "x", "quantity": 1, "unitPrice": 10, "discountValue": 2 }""", "discountValue")]
    [InlineData("""{ "description": " ", "quantity": 1, "unitPrice": 10 }""", "Description")]
    public async Task InvalidLines_Are422(string json, string field)
    {
        var clinic = await ClinicAsync();
        var draft = await DraftAsync(clinic);
        var body = json.Replace("}", $", \"taxRateId\": \"{clinic.Standard}\" }}");

        var response = await clinic.Desk.PostAsync($"/api/invoices/{draft}/items", Json(body));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, d => string.Equals(d.Field, field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PartiesTaxRatesAndConsultationLinks_AreChecked()
    {
        var clinic = await ClinicAsync();
        var otherClient = await ClientAsync(clinic.Desk);
        var notTheirs = await clinic.Desk.PostAsJsonAsync("/api/invoices", new { clientId = otherClient, patientId = clinic.PatientId });
        Assert.Equal("patientId", Assert.Single((await ErrorOf(notTheirs)).Details).Field);

        var draft = await DraftAsync(clinic);
        var archived = (await OkAsync(await clinic.Admin.PostAsJsonAsync("/api/tax-rates", new { name = "Old", ratePercent = 5 }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Admin.PatchAsJsonAsync($"/api/tax-rates/{archived}", new { status = "Archived" }));
        foreach (var taxRateId in new[] { archived, Guid.NewGuid() })
        {
            var response = await clinic.Desk.PostAsJsonAsync($"/api/invoices/{draft}/items", new { description = "x", quantity = 1, unitPrice = 1, taxRateId });
            Assert.Equal("taxRateId", Assert.Single((await ErrorOf(response)).Details).Field);
        }

        // A line may bill a consultation of the invoice's patient.
        var consultation = (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var linked = await AddLineAsync(clinic, draft, new { description = "Exam", quantity = 1, unitPrice = 45, taxRateId = clinic.Standard, consultationId = consultation });
        Assert.Equal(consultation, linked.GetProperty("items")[0].GetProperty("consultationId").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Desk.PatchAsync($"/api/invoices/{draft}", Json("""{ "patientId": null }"""))).StatusCode);

        var noPatient = await DraftAsync(clinic, withPatient: false);
        var unlinkable = await clinic.Desk.PostAsJsonAsync($"/api/invoices/{noPatient}/items", new { description = "Exam", quantity = 1, unitPrice = 45, taxRateId = clinic.Standard, consultationId = consultation });
        Assert.Equal("consultationId", Assert.Single((await ErrorOf(unlinkable)).Details).Field);

        // Renaming a rate later does not touch the copy on the line.
        await OkAsync(await clinic.Admin.PatchAsJsonAsync($"/api/tax-rates/{clinic.Standard}", new { name = "Standard VAT" }));
        var read = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/invoices/{draft}");
        Assert.Equal("Standard", read.GetProperty("items")[0].GetProperty("taxRate").GetProperty("name").GetString());
    }

    // ---------- Currency and tax rates ----------

    [Fact]
    public async Task TheClinicCurrency_IsRequired_Iso4217_AndFixedOnceInvoicesExist()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        var clientId = await ClientAsync(desk);

        Assert.Equal(HttpStatusCode.Conflict, (await desk.PostAsJsonAsync("/api/invoices", new { clientId })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PatchAsJsonAsync("/api/clinic", new { currency = "eur" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PatchAsJsonAsync("/api/clinic", new { currency = "XXX" })).StatusCode);
        var set = await OkAsync(await admin.PatchAsJsonAsync("/api/clinic", new { currency = "JPY" }));
        Assert.Equal("JPY", set.GetProperty("currency").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PatchAsJsonAsync("/api/clinic", new { currency = "EUR" })).StatusCode);

        var invoice = await OkAsync(await desk.PostAsJsonAsync("/api/invoices", new { clientId }), HttpStatusCode.Created);
        Assert.Equal("JPY", invoice.GetProperty("currency").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync("/api/clinic", new { currency = "EUR" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync("/api/clinic", new { currency = "JPY" })).StatusCode); // unchanged is fine
    }

    [Fact]
    public async Task TaxRates_HaveUniqueActiveNames_AndFixedPercentages()
    {
        var clinic = await ClinicAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Admin.PostAsJsonAsync("/api/tax-rates", new { name = " standard ", ratePercent = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Admin.PostAsJsonAsync("/api/tax-rates", new { name = "Odd", ratePercent = 100.5 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Admin.PostAsJsonAsync("/api/tax-rates", new { name = "Odd", ratePercent = 1.23456 })).StatusCode);
        await OkAsync(await clinic.Admin.PatchAsJsonAsync($"/api/tax-rates/{clinic.Standard}", new { status = "Archived" }));
        var replacement = await OkAsync(await clinic.Admin.PostAsJsonAsync("/api/tax-rates", new { name = "Standard", ratePercent = 21 }), HttpStatusCode.Created);
        Assert.Equal(21m, Amount(replacement, "ratePercent"));
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Admin.PatchAsJsonAsync($"/api/tax-rates/{clinic.Standard}", new { status = "Active" })).StatusCode);

        var active = await clinic.Desk.GetFromJsonAsync<JsonElement>("/api/tax-rates?status=Active");
        Assert.Equal(["Standard", "Zero"], active.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        Assert.Contains(AuditActions.TaxRateCreated, (await factory.AuditEntriesAsync(replacement.GetProperty("id").GetGuid())).Select(e => e.Action));
    }

    // ---------- Permissions and tenancy ----------

    [Fact]
    public async Task Permissions_FollowTheBillingRoles()
    {
        var clinic = await ClinicAsync();
        var draft = await DraftAsync(clinic);
        var inventory = await factory.SignInAsync(await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Inventory));

        Assert.Equal(HttpStatusCode.OK, (await clinic.Vet.GetAsync($"/api/invoices/{draft}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clinic.Admin.GetAsync("/api/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Vet.PostAsJsonAsync("/api/invoices", new { clientId = clinic.ClientId })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Vet.PostAsync($"/api/invoices/{draft}/issue", null)).StatusCode);
        foreach (var http in new[] { clinic.Nurse, inventory })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/api/invoices/{draft}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/tax-rates")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.PostAsJsonAsync("/api/tax-rates", new { name = "Mine", ratePercent = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clinic.Desk.GetAsync("/api/tax-rates")).StatusCode);
        var me = await clinic.Vet.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Contains(Permissions.BillingRead, me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task OtherClinicsInvoicesClientsAndRates_AreInvisible()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var theirs = await DraftAsync(other);

        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.GetAsync($"/api/invoices/{theirs}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.PatchAsJsonAsync($"/api/invoices/{theirs}", new { notes = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.PostAsync($"/api/invoices/{theirs}/issue", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.DeleteAsync($"/api/invoices/{theirs}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Admin.PatchAsJsonAsync($"/api/tax-rates/{other.Standard}", new { name = "x" })).StatusCode);
        var theirClient = await clinic.Desk.PostAsJsonAsync("/api/invoices", new { clientId = other.ClientId });
        Assert.Equal("clientId", Assert.Single((await ErrorOf(theirClient)).Details).Field);
        var mine = await DraftAsync(clinic);
        var theirRate = await clinic.Desk.PostAsJsonAsync($"/api/invoices/{mine}/items", new { description = "x", quantity = 1, unitPrice = 1, taxRateId = other.Standard });
        Assert.Equal("taxRateId", Assert.Single((await ErrorOf(theirRate)).Details).Field);
        var list = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/invoices?clientId={other.ClientId}");
        Assert.Equal(0, list.GetProperty("pagination").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task History_ShowsIssuedInvoices_ToBillingReadersOnly()
    {
        var clinic = await ClinicAsync();
        var issued = await DraftAsync(clinic);
        await AddLineAsync(clinic, issued, Exam(clinic));
        await IssueAsync(clinic, issued);
        await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{issued}/void", new { reason = "x" }));
        await DraftAsync(clinic); // drafts never appear

        var forDesk = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");
        var forNurse = await clinic.Nurse.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");

        var entry = Assert.Single(forDesk.GetProperty("data").EnumerateArray(), e => e.GetProperty("type").GetString() == "InvoiceIssued");
        var details = entry.GetProperty("details").GetProperty("invoice");
        Assert.Equal(["id", "number", "total", "currency", "status", "paymentStatus", "balanceDue"], details.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("Void", 54m), (details.GetProperty("status").GetString(), details.GetProperty("total").GetDecimal()));
        Assert.DoesNotContain(forNurse.GetProperty("data").EnumerateArray(), e => e.GetProperty("type").GetString() == "InvoiceIssued");
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE invoices SET total = total + 1, subtotal = subtotal + 1 WHERE id = {0}")]
    [InlineData("UPDATE invoices SET notes = 'changed' WHERE id = {0}")]
    [InlineData("DELETE FROM invoice_items WHERE invoice_id = {0}")]
    [InlineData("DELETE FROM invoices WHERE id = {0}")]
    [InlineData("UPDATE invoice_items SET unit_price = unit_price + 1 WHERE invoice_id = {0}")]
    [InlineData("INSERT INTO invoice_items (id, clinic_id, invoice_id, position, description, quantity, unit_price, discount_type, discount_value, tax_rate_id, tax_rate_name, tax_rate_percent, net, discount, tax, total, created_at, updated_at) SELECT gen_random_uuid(), clinic_id, invoice_id, 99, 'late', quantity, unit_price, discount_type, discount_value, tax_rate_id, tax_rate_name, tax_rate_percent, net, discount, tax, total, now(), now() FROM invoice_items WHERE invoice_id = {0}")]
    [InlineData("UPDATE invoices SET status = 'Draft', number = NULL, issued_at = NULL, due_on = NULL, issued_by_user_id = NULL WHERE id = {0}")]
    [InlineData("TRUNCATE invoice_items")]
    public async Task Database_FreezesIssuedInvoices(string sql)
    {
        var clinic = await ClinicAsync();
        var id = await DraftAsync(clinic);
        await AddLineAsync(clinic, id, Exam(clinic));
        await IssueAsync(clinic, id);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Database_RefusesIssuingWithWrongTotals_AndMovingLines()
    {
        var clinic = await ClinicAsync();
        var draft = await DraftAsync(clinic);
        await AddLineAsync(clinic, draft, Exam(clinic));
        var other = await DraftAsync(clinic);

        var wrongTotals = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE invoices SET status = 'Issued', number = 999, issued_at = now(), due_on = current_date, issued_by_user_id = created_by_user_id, " +
            "subtotal = 1, total = 1, tax_total = 0 WHERE id = {0}", draft)));
        Assert.Equal("42501", wrongTotals.SqlState);

        var moved = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE invoice_items SET invoice_id = {0} WHERE invoice_id = {1}", other, draft)));
        Assert.Equal("42501", moved.SqlState);

        var issuedInsert = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO invoices (id, clinic_id, client_id, status, currency, subtotal, discount_total, tax_total, total, number, issued_at, due_on, issued_by_user_id, created_at, updated_at) " +
            "SELECT gen_random_uuid(), clinic_id, client_id, 'Issued', currency, 0, 0, 0, 0, 998, now(), current_date, created_by_user_id, now(), now() FROM invoices WHERE id = {0}", draft)));
        Assert.Equal("42501", issuedInsert.SqlState);

        var inconsistent = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE invoice_items SET total = total + 1 WHERE invoice_id = {0}", draft)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, inconsistent.SqlState);
    }

    // ---------- Review follow-ups ----------

    [Fact]
    public async Task AmountsBeyondTheMaximum_Are422_NotAnOverflow()
    {
        var clinic = await ClinicAsync();
        var draft = await DraftAsync(clinic);

        var huge = await clinic.Desk.PostAsJsonAsync($"/api/invoices/{draft}/items", new { description = "x", quantity = 1_000_000, unitPrice = 1_000_000_000, taxRateId = clinic.Standard });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, huge.StatusCode);
        Assert.Contains("cannot exceed", Assert.Single((await ErrorOf(huge)).Details).Message);

        // Each line fits, but together they would pass the invoice maximum.
        await AddLineAsync(clinic, draft, new { description = "a", quantity = 600, unitPrice = 1_000_000_000m, taxRateId = clinic.Zero });
        var second = await clinic.Desk.PostAsJsonAsync($"/api/invoices/{draft}/items", new { description = "b", quantity = 600, unitPrice = 1_000_000_000m, taxRateId = clinic.Zero });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }

    [Fact]
    public async Task Issuing_IsRefused_WhenThePatientChangedOwner()
    {
        var clinic = await ClinicAsync();
        var draft = await DraftAsync(clinic);
        await AddLineAsync(clinic, draft, Exam(clinic));
        var newOwner = await ClientAsync(clinic.Desk);
        await OkAsync(await clinic.Desk.PatchAsJsonAsync($"/api/patients/{clinic.PatientId}", new { clientId = newOwner }));

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Desk.PostAsync($"/api/invoices/{draft}/issue", null)).StatusCode);
        await OkAsync(await clinic.Desk.PatchAsJsonAsync($"/api/invoices/{draft}", new { clientId = newOwner }));
        await IssueAsync(clinic, draft);
    }

    [Fact]
    public async Task MoreTenancyAndPermissionChecks()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var theirs = await DraftAsync(other);
        var theirConsultation = (await OkAsync(await other.Vet.PostAsJsonAsync("/api/consultations", new { patientId = other.PatientId, veterinarianId = other.VetUser.Id }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var mine = await DraftAsync(clinic);

        var theirPatient = await clinic.Desk.PostAsJsonAsync("/api/invoices", new { clientId = clinic.ClientId, patientId = other.PatientId });
        Assert.Equal("patientId", Assert.Single((await ErrorOf(theirPatient)).Details).Field);
        var linkTheirs = await clinic.Desk.PostAsJsonAsync($"/api/invoices/{mine}/items", new { description = "x", quantity = 1, unitPrice = 1, taxRateId = clinic.Standard, consultationId = theirConsultation });
        Assert.Equal("consultationId", Assert.Single((await ErrorOf(linkTheirs)).Details).Field);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.PostAsJsonAsync($"/api/invoices/{theirs}/items", Exam(clinic))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.PostAsJsonAsync($"/api/invoices/{theirs}/void", new { reason = "x" })).StatusCode);

        var line = (await AddLineAsync(clinic, mine, Exam(clinic))).GetProperty("items")[0].GetProperty("id").GetGuid();
        var vetAttempts = new[]
        {
            await clinic.Vet.PatchAsJsonAsync($"/api/invoices/{mine}", new { notes = "x" }),
            await clinic.Vet.PostAsJsonAsync($"/api/invoices/{mine}/items", Exam(clinic)),
            await clinic.Vet.PatchAsJsonAsync($"/api/invoices/{mine}/items/{line}", new { quantity = 2 }),
            await clinic.Vet.DeleteAsync($"/api/invoices/{mine}/items/{line}"),
            await clinic.Vet.PostAsJsonAsync($"/api/invoices/{mine}/void", new { reason = "x" }),
            await clinic.Vet.DeleteAsync($"/api/invoices/{mine}"),
            await clinic.Vet.PostAsJsonAsync("/api/tax-rates", new { name = "Vet", ratePercent = 1 }),
            await clinic.Vet.PatchAsJsonAsync("/api/clinic", new { currency = "USD" }),
        };
        Assert.All(vetAttempts, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Admin.PatchAsJsonAsync($"/api/tax-rates/{Guid.NewGuid()}", new { name = " " })).StatusCode);
    }

    [Fact]
    public async Task Database_GuardsTaxRates_AndBilledConsultations()
    {
        var clinic = await ClinicAsync();
        var percent = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE tax_rates SET rate_percent = 21 WHERE id = {0}", clinic.Standard)));
        Assert.Equal("42501", percent.SqlState);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM tax_rates WHERE id = {0}", clinic.Zero)));
        Assert.Equal("42501", delete.SqlState);
        await factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE tax_rates SET name = 'Renamed' WHERE id = {0}", clinic.Zero));

        var draft = await DraftAsync(clinic);
        await AddLineAsync(clinic, draft, Exam(clinic));
        var otherPatient = await PatientAsync(clinic.Desk, clinic.ClientId, "Tom");
        var otherPatientsConsultation = (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = otherPatient, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var discarded = (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{discarded}/discard", new { reason = "x" }));

        foreach (var consultation in new[] { otherPatientsConsultation, discarded })
        {
            var link = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
                db.Database.ExecuteSqlRawAsync("UPDATE invoice_items SET consultation_id = {0} WHERE invoice_id = {1}", consultation, draft)));
            Assert.Equal("42501", link.SqlState);
        }
    }
}
