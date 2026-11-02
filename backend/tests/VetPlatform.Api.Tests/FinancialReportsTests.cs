using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Financial reports and the client statement (B-24) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class FinancialReportsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private sealed record Clinic(HttpClient Admin, User AdminUser, HttpClient Desk, HttpClient Vet, HttpClient Nurse, Guid Jane, Guid Amy, Guid Standard, Guid Zero);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        var vet = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian));
        var nurse = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Nurse));
        await OkAsync(await admin.PatchAsJsonAsync("/api/clinic", new { currency = "EUR" }));
        var standard = (await OkAsync(await admin.PostAsJsonAsync("/api/tax-rates", new { name = "Standard", ratePercent = 20 }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var zero = (await OkAsync(await admin.PostAsJsonAsync("/api/tax-rates", new { name = "Zero", ratePercent = 0 }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        return new Clinic(admin, adminUser, desk, vet, nurse, await ClientAsync(desk, "Jane", "Doe"), await ClientAsync(desk, "Amy", "Zed"), standard, zero);
    }

    private static async Task<Guid> ClientAsync(HttpClient desk, string firstName, string lastName) =>
        (await OkAsync(await desk.PostAsJsonAsync("/api/clients", new { firstName, lastName, city = "Leeds" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private DateOnly Today => DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);

    /// <summary>An invoice with the given lines (unit price, tax rate, optional percent discount), issued unless <paramref name="issue"/> is false.</summary>
    private static async Task<Guid> InvoiceAsync(Clinic clinic, Guid clientId, (decimal Price, Guid Rate, decimal? Discount)[] lines, bool issue = true, string? dueOn = null)
    {
        var id = (await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/invoices", new { clientId }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        foreach (var (price, rate, discount) in lines)
        {
            await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{id}/items", discount is { } percent
                ? new { description = "Item", quantity = 1m, unitPrice = price, taxRateId = rate, discountType = "Percent", discountValue = percent }
                : (object)new { description = "Item", quantity = 1m, unitPrice = price, taxRateId = rate }));
        }

        if (issue)
        {
            await OkAsync(dueOn is null
                ? await clinic.Desk.PostAsync($"/api/invoices/{id}/issue", null)
                : await clinic.Desk.PostAsJsonAsync($"/api/invoices/{id}/issue", new { dueOn }));
        }

        return id;
    }

    /// <summary>54.00 EUR (45 + 20% tax).</summary>
    private static Task<Guid> ExamAsync(Clinic clinic, Guid clientId, string? dueOn = null) => InvoiceAsync(clinic, clientId, [(45, clinic.Standard, null)], dueOn: dueOn);

    private static async Task<Guid> PayAsync(Clinic clinic, Guid invoiceId, decimal amount, string method = "Cash") =>
        (await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{invoiceId}/payments", new { amount, method }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<Guid> RefundAsync(Clinic clinic, Guid invoiceId, Guid paymentId, decimal amount, string method = "Card") =>
        (await OkAsync(await clinic.Admin.PostAsJsonAsync($"/api/invoices/{invoiceId}/payments/{paymentId}/refunds", new { amount, method, reason = "Goodwill" }), HttpStatusCode.Created))
            .GetProperty("refunds").EnumerateArray().Last().GetProperty("id").GetGuid();

    /// <summary>Moves a recorded timestamp (or date) into the past; the guards that keep them "now" are bypassed for the seed.</summary>
    private Task BackdateAsync(string table, Guid id, string column, string value)
    {
        var sql = "BEGIN; SET LOCAL session_replication_role = replica; UPDATE " + table + " SET " + column + " = {1} WHERE id = {0}; COMMIT;";
        return factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            sql, id, value.Contains('T') ? DateTimeOffset.Parse(value) : DateOnly.Parse(value)));
    }

    private static decimal Number(JsonElement e, string p) => e.GetProperty(p).GetDecimal();

    private static int Int(JsonElement e, string p) => e.GetProperty(p).GetInt32();

    // ---------- Revenue ----------

    [Fact]
    public async Task Revenue_CountsIssuedInvoicesOnTheirIssueDay_ByTaxRate_WithVoidsApart()
    {
        var clinic = await ClinicAsync();
        var a = await ExamAsync(clinic, clinic.Jane);
        var voided = await ExamAsync(clinic, clinic.Amy);
        await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{voided}/void", new { reason = "Wrong client" }));
        var c = await InvoiceAsync(clinic, clinic.Jane, [(100, clinic.Standard, 10), (10, clinic.Zero, null)]); // 90 + 18 tax, and 10
        var later = await ExamAsync(clinic, clinic.Jane);
        await InvoiceAsync(clinic, clinic.Jane, [(500, clinic.Standard, null)], issue: false); // drafts never count
        await BackdateAsync("invoices", a, "issued_at", "2026-05-04T10:00:00Z");
        await BackdateAsync("invoices", voided, "issued_at", "2026-05-05T09:00:00Z");
        await BackdateAsync("invoices", voided, "voided_at", "2026-05-05T12:00:00Z");
        await BackdateAsync("invoices", c, "issued_at", "2026-05-12T10:00:00Z");
        await BackdateAsync("invoices", later, "issued_at", "2026-06-01T10:00:00Z");

        var report = await OkAsync(await clinic.Admin.GetAsync("/api/reports/revenue?from=2026-05-01&to=2026-05-31&groupBy=Week"));
        Assert.Equal(["from", "to", "groupBy", "timeZone", "currency", "totals", "byTaxRate", "buckets"], report.EnumerateObject().Select(p => p.Name));
        Assert.Equal("EUR", report.GetProperty("currency").GetString());
        var totals = report.GetProperty("totals");
        Assert.Equal(["invoices", "subtotal", "discountTotal", "taxTotal", "total", "voidedInvoices", "voidedTotal"], totals.EnumerateObject().Select(p => p.Name));
        Assert.Equal((2, 155m, 10m, 27m, 172m, 1, 54m),
            (Int(totals, "invoices"), Number(totals, "subtotal"), Number(totals, "discountTotal"), Number(totals, "taxTotal"), Number(totals, "total"),
                Int(totals, "voidedInvoices"), Number(totals, "voidedTotal")));
        Assert.Equal("172", totals.GetProperty("total").GetRawText()); // shortest form, as everywhere

        var rates = report.GetProperty("byTaxRate").EnumerateArray().ToList();
        Assert.Equal(["Zero", "Standard"], rates.Select(r => r.GetProperty("taxRate").GetProperty("name").GetString()));
        Assert.Equal((10m, 0m, 0m, 10m), (Number(rates[0], "net"), Number(rates[0], "discount"), Number(rates[0], "tax"), Number(rates[0], "total")));
        Assert.Equal((145m, 10m, 27m, 162m, 20m),
            (Number(rates[1], "net"), Number(rates[1], "discount"), Number(rates[1], "tax"), Number(rates[1], "total"), Number(rates[1].GetProperty("taxRate"), "percent")));

        var buckets = report.GetProperty("buckets").EnumerateArray().Select(b => b.GetProperty("totals")).ToList();
        Assert.Equal([0m, 54m, 118m, 0m, 0m], buckets.Select(b => Number(b, "total")));
        Assert.Equal([0, 1, 0, 0, 0], buckets.Select(b => Int(b, "voidedInvoices")));
    }

    // ---------- Payments ----------

    [Fact]
    public async Task Payments_AreCountedOnTheirDay_ByMethod_LessRefunds()
    {
        var clinic = await ClinicAsync();
        var invoice = await ExamAsync(clinic, clinic.Jane);
        var cash = await PayAsync(clinic, invoice, 20, "Cash");
        var card = await PayAsync(clinic, invoice, 34, "Card");
        var refund = await RefundAsync(clinic, invoice, card, 5);
        var other = await ExamAsync(clinic, clinic.Amy);
        var outside = await PayAsync(clinic, other, 54, "BankTransfer");
        foreach (var id in new[] { invoice, other })
        {
            await BackdateAsync("invoices", id, "issued_at", "2026-04-30T10:00:00Z");
        }

        await BackdateAsync("payments", cash, "paid_at", "2026-05-04T10:00:00Z");
        await BackdateAsync("payments", card, "paid_at", "2026-05-11T10:00:00Z");
        await BackdateAsync("refunds", refund, "refunded_at", "2026-05-12T10:00:00Z");
        await BackdateAsync("payments", outside, "paid_at", "2026-06-01T10:00:00Z");

        var report = await OkAsync(await clinic.Admin.GetAsync("/api/reports/payments?from=2026-05-01&to=2026-05-31&groupBy=Week"));
        Assert.Equal(["from", "to", "groupBy", "timeZone", "currency", "totals", "byMethod", "buckets"], report.EnumerateObject().Select(p => p.Name));
        var totals = report.GetProperty("totals");
        Assert.Equal(["payments", "received", "refunds", "refunded", "net"], totals.EnumerateObject().Select(p => p.Name));
        Assert.Equal((2, 54m, 1, 5m, 49m), (Int(totals, "payments"), Number(totals, "received"), Int(totals, "refunds"), Number(totals, "refunded"), Number(totals, "net")));

        var methods = report.GetProperty("byMethod").EnumerateArray().ToList();
        Assert.Equal(["Cash", "Card"], methods.Select(m => m.GetProperty("method").GetString()));
        Assert.Equal((1, 20m, 0, 20m), (Int(methods[0].GetProperty("totals"), "payments"), Number(methods[0].GetProperty("totals"), "received"),
            Int(methods[0].GetProperty("totals"), "refunds"), Number(methods[0].GetProperty("totals"), "net")));
        Assert.Equal((34m, 5m, 29m), (Number(methods[1].GetProperty("totals"), "received"), Number(methods[1].GetProperty("totals"), "refunded"), Number(methods[1].GetProperty("totals"), "net")));

        var buckets = report.GetProperty("buckets").EnumerateArray().Select(b => b.GetProperty("totals")).ToList();
        Assert.Equal([0m, 20m, 29m, 0m, 0m], buckets.Select(b => Number(b, "net")));
    }

    // ---------- Aged receivables ----------

    [Fact]
    public async Task Receivables_AgeBalancesByDaysPastDue_PerClient_AndAreAudited()
    {
        var clinic = await ClinicAsync();
        var today = Today;
        await ExamAsync(clinic, clinic.Jane, today.AddDays(10).ToString("yyyy-MM-dd")); // current
        await ExamAsync(clinic, clinic.Jane); // due today: current
        var partial = await ExamAsync(clinic, clinic.Jane);
        await PayAsync(clinic, partial, 20); // 34 left
        var old = await ExamAsync(clinic, clinic.Jane);
        var thirty = await ExamAsync(clinic, clinic.Amy);
        var sixty = await ExamAsync(clinic, clinic.Amy);
        var ninety = await ExamAsync(clinic, clinic.Amy);
        var paid = await ExamAsync(clinic, clinic.Amy);
        await PayAsync(clinic, paid, 54); // nothing owed
        await InvoiceAsync(clinic, clinic.Amy, [(45, clinic.Standard, null)], issue: false);
        foreach (var (id, days) in new[] { (partial, 15), (old, 91), (thirty, 30), (sixty, 60), (ninety, 90), (paid, 200) })
        {
            await BackdateAsync("invoices", id, "due_on", today.AddDays(-days).ToString("yyyy-MM-dd"));
        }

        var report = await OkAsync(await clinic.Admin.GetAsync("/api/reports/receivables"));
        Assert.Equal(["asOf", "timeZone", "currency", "totals", "clients"], report.EnumerateObject().Select(p => p.Name));
        Assert.Equal(today.ToString("yyyy-MM-dd"), report.GetProperty("asOf").GetString());
        var totals = report.GetProperty("totals");
        Assert.Equal(["invoices", "current", "days1To30", "days31To60", "days61To90", "over90Days", "total"], totals.EnumerateObject().Select(p => p.Name));
        Assert.Equal((7, 108m, 88m, 54m, 54m, 54m, 358m),
            (Int(totals, "invoices"), Number(totals, "current"), Number(totals, "days1To30"), Number(totals, "days31To60"), Number(totals, "days61To90"),
                Number(totals, "over90Days"), Number(totals, "total")));

        // Largest balance first.
        var clients = report.GetProperty("clients").EnumerateArray().ToList();
        Assert.Equal([clinic.Jane, clinic.Amy], clients.Select(c => c.GetProperty("client").GetProperty("id").GetGuid()));
        Assert.Equal(["client", "aging", "oldestDueOn"], clients[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal((4, 108m, 34m, 54m, 196m), (Int(clients[0].GetProperty("aging"), "invoices"), Number(clients[0].GetProperty("aging"), "current"),
            Number(clients[0].GetProperty("aging"), "days1To30"), Number(clients[0].GetProperty("aging"), "over90Days"), Number(clients[0].GetProperty("aging"), "total")));
        Assert.Equal(today.AddDays(-91).ToString("yyyy-MM-dd"), clients[0].GetProperty("oldestDueOn").GetString());
        Assert.Equal("Zed", clients[1].GetProperty("client").GetProperty("lastName").GetString());

        var entry = (await factory.AuditEntriesAsync(clinic.AdminUser.ClinicId)).Single(e => e.Action == AuditActions.ReceivablesViewed);
        Assert.Contains(clinic.Jane.ToString(), entry.Metadata);
        Assert.DoesNotContain("Doe", entry.Metadata);
    }

    // ---------- Client statement ----------

    [Fact]
    public async Task Statement_ShowsOpeningBalance_EveryChangeWithARunningBalance_AndClosingBalance()
    {
        var clinic = await ClinicAsync();
        var first = await ExamAsync(clinic, clinic.Jane);
        var early = await PayAsync(clinic, first, 20);
        var second = await ExamAsync(clinic, clinic.Jane);
        var rest = await PayAsync(clinic, first, 34, "Card");
        var voided = await ExamAsync(clinic, clinic.Jane);
        await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{voided}/void", new { reason = "Duplicate" }));
        var full = await PayAsync(clinic, second, 54, "Card");
        var refund = await RefundAsync(clinic, second, full, 10);
        var after = await ExamAsync(clinic, clinic.Jane);
        await ExamAsync(clinic, clinic.Amy); // another client's
        await InvoiceAsync(clinic, clinic.Jane, [(45, clinic.Standard, null)], issue: false);

        await BackdateAsync("invoices", first, "issued_at", "2026-04-20T10:00:00Z");
        await BackdateAsync("payments", early, "paid_at", "2026-04-25T10:00:00Z");
        await BackdateAsync("invoices", second, "issued_at", "2026-05-03T10:00:00Z");
        await BackdateAsync("payments", rest, "paid_at", "2026-05-04T10:00:00Z");
        await BackdateAsync("invoices", voided, "issued_at", "2026-05-05T10:00:00Z");
        await BackdateAsync("invoices", voided, "voided_at", "2026-05-06T10:00:00Z");
        await BackdateAsync("payments", full, "paid_at", "2026-05-10T10:00:00Z");
        await BackdateAsync("refunds", refund, "refunded_at", "2026-05-12T10:00:00Z");
        await BackdateAsync("invoices", after, "issued_at", "2026-06-02T10:00:00Z");

        var statement = await OkAsync(await clinic.Desk.GetAsync($"/api/clients/{clinic.Jane}/statement?from=2026-05-01&to=2026-05-31"));
        Assert.Equal(["client", "from", "to", "timeZone", "currency", "openingBalance", "totals", "closingBalance", "entries"], statement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("Doe", "Leeds"), (statement.GetProperty("client").GetProperty("lastName").GetString(), statement.GetProperty("client").GetProperty("city").GetString()));
        Assert.Equal((34m, 10m), (Number(statement, "openingBalance"), Number(statement, "closingBalance")));
        var totals = statement.GetProperty("totals");
        Assert.Equal((108m, 88m, 10m, 54m), (Number(totals, "invoiced"), Number(totals, "paid"), Number(totals, "refunded"), Number(totals, "voided")));

        var entries = statement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(["type", "occurredAt", "invoice", "dueOn", "paymentId", "refundId", "method", "amount", "balance"], entries[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["InvoiceIssued", "Payment", "InvoiceIssued", "InvoiceVoided", "Payment", "Refund"], entries.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal([54m, -34m, 54m, -54m, -54m, 10m], entries.Select(e => Number(e, "amount")));
        Assert.Equal([88m, 54m, 108m, 54m, 0m, 10m], entries.Select(e => Number(e, "balance")));
        Assert.Equal(first, entries[1].GetProperty("invoice").GetProperty("id").GetGuid());
        Assert.Equal((full, refund, "Card"), (entries[5].GetProperty("paymentId").GetGuid(), entries[5].GetProperty("refundId").GetGuid(), entries[5].GetProperty("method").GetString()));

        // The balance at the end of the period matches what the invoices say once later activity is left out.
        var invoice = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/invoices/{second}");
        Assert.Equal(10m, Number(invoice, "balanceDue"));

        var entry = (await factory.AuditEntriesAsync(clinic.Jane)).Single(e => e.Action == AuditActions.ClientStatementViewed);
        Assert.Contains("2026-05-01", entry.Metadata);
    }

    [Fact]
    public async Task Statement_NeedsBillingRead_AndStaysInTheClinic()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        const string query = "statement?from=2026-05-01&to=2026-05-31";

        Assert.Equal(HttpStatusCode.OK, (await clinic.Vet.GetAsync($"/api/clients/{clinic.Jane}/{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Nurse.GetAsync($"/api/clients/{clinic.Jane}/{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Admin.GetAsync($"/api/clients/{other.Jane}/{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Admin.GetAsync($"/api/clients/{Guid.NewGuid()}/{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Admin.GetAsync($"/api/clients/{clinic.Jane}/statement?from=2026-05-31&to=2026-05-01")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Admin.GetAsync($"/api/clients/{clinic.Jane}/statement?to=2026-05-01")).StatusCode);

        var empty = await OkAsync(await clinic.Admin.GetAsync($"/api/clients/{clinic.Jane}/{query}"));
        Assert.Equal((0m, 0m, 0), (Number(empty, "openingBalance"), Number(empty, "closingBalance"), empty.GetProperty("entries").GetArrayLength()));
    }

    // ---------- Rules ----------

    [Fact]
    public async Task FinancialReports_AreForAdministrators_AndStayInTheClinic()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var invoice = await ExamAsync(other, other.Jane);
        await PayAsync(other, invoice, 10);
        var from = Today.AddDays(-1).ToString("yyyy-MM-dd");
        var to = Today.AddDays(1).ToString("yyyy-MM-dd");

        var revenue = await OkAsync(await clinic.Admin.GetAsync($"/api/reports/revenue?from={from}&to={to}"));
        Assert.Equal((0, 3), (Int(revenue.GetProperty("totals"), "invoices"), revenue.GetProperty("buckets").GetArrayLength()));
        Assert.Equal(0, Int((await OkAsync(await clinic.Admin.GetAsync($"/api/reports/payments?from={from}&to={to}"))).GetProperty("totals"), "payments"));
        Assert.Equal(0, (await OkAsync(await clinic.Admin.GetAsync("/api/reports/receivables"))).GetProperty("clients").GetArrayLength());
        Assert.Equal(1, Int((await OkAsync(await other.Admin.GetAsync($"/api/reports/revenue?from={from}&to={to}"))).GetProperty("totals"), "invoices"));

        foreach (var url in new[] { $"/api/reports/revenue?from={from}&to={to}", $"/api/reports/payments?from={from}&to={to}", "/api/reports/receivables" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Vet.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Admin.GetAsync("/api/reports/revenue?from=2026-01-01&to=2027-01-02")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Admin.GetAsync("/api/reports/payments?from=2026-01-01&to=2026-01-31&groupBy=month")).StatusCode);
    }
}
