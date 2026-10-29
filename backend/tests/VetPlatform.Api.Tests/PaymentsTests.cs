using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Features.Billing;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Payments, refunds and invoice balances (B-17) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class PaymentsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private sealed record Clinic(HttpClient Admin, User AdminUser, HttpClient Vet, HttpClient Nurse, HttpClient Desk, Guid ClientId, Guid PatientId, Guid Standard);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var vet = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian));
        var nurse = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Nurse));
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        await OkAsync(await admin.PatchAsJsonAsync("/api/clinic", new { currency = "EUR" }));
        var standard = (await OkAsync(await admin.PostAsJsonAsync("/api/tax-rates", new { name = "Standard", ratePercent = 20 }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var client = await desk.PostAsJsonAsync("/api/clients", new { firstName = "Jane", lastName = "Doe" });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var patient = await desk.PostAsJsonAsync("/api/patients", new { clientId, name = "Rex", species = "Dog" });
        var patientId = (await patient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return new Clinic(admin, adminUser, vet, nurse, desk, clientId, patientId, standard);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>An issued invoice of 54.00 EUR (45 + 20% tax).</summary>
    private static async Task<Guid> IssuedAsync(Clinic clinic, string? dueOn = null)
    {
        var id = (await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/invoices", new { clientId = clinic.ClientId, patientId = clinic.PatientId }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{id}/items", new { description = "Exam", quantity = 1, unitPrice = 45, taxRateId = clinic.Standard }));
        await OkAsync(dueOn is null
            ? await clinic.Desk.PostAsync($"/api/invoices/{id}/issue", null)
            : await clinic.Desk.PostAsJsonAsync($"/api/invoices/{id}/issue", new { dueOn }));
        return id;
    }

    private static Task<HttpResponseMessage> PayAsync(Clinic clinic, Guid invoice, object body) =>
        clinic.Desk.PostAsJsonAsync($"/api/invoices/{invoice}/payments", body);

    private static decimal Amount(JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    // ---------- Payments ----------

    [Fact]
    public async Task PartialThenFullPayment_UpdatesTheBalance()
    {
        var clinic = await ClinicAsync();
        var id = await IssuedAsync(clinic);
        var issued = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}");
        Assert.Equal(("Unpaid", 0m, 54m), (issued.GetProperty("paymentStatus").GetString(), Amount(issued, "amountPaid"), Amount(issued, "balanceDue")));

        var first = await OkAsync(await PayAsync(clinic, id, new { amount = 20, method = "Cash" }), HttpStatusCode.Created);
        Assert.Equal(
            ["id", "amount", "method", "reference", "paidAt", "receivedBy", "refundedAmount", "refunds", "invoice", "createdAt"],
            first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("PartiallyPaid", 20m, 34m),
            (first.GetProperty("invoice").GetProperty("paymentStatus").GetString(), Amount(first.GetProperty("invoice"), "amountPaid"), Amount(first.GetProperty("invoice"), "balanceDue")));

        var second = await OkAsync(await PayAsync(clinic, id, new { amount = 34, method = "Card", reference = " Slip 0042 " }), HttpStatusCode.Created);
        Assert.Equal("Slip 0042", second.GetProperty("reference").GetString());
        Assert.Equal("Paid", second.GetProperty("invoice").GetProperty("paymentStatus").GetString());

        var invoice = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}");
        Assert.Equal(("Paid", 54m, 0m), (invoice.GetProperty("paymentStatus").GetString(), Amount(invoice, "amountPaid"), Amount(invoice, "balanceDue")));
        var payments = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/payments");
        Assert.Equal([20m, 34m], payments.EnumerateArray().Select(p => Amount(p, "amount")));
        var entry = (await factory.AuditEntriesAsync(second.GetProperty("id").GetGuid())).Single();
        Assert.Equal(AuditActions.PaymentRecorded, entry.Action);
        Assert.DoesNotContain("Slip", entry.Metadata);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PayAsync(clinic, id, new { amount = 1, method = "Cash" })).StatusCode); // balance is 0
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Desk.PostAsJsonAsync($"/api/invoices/{id}/void", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task InvalidPayments_AreRefused()
    {
        var clinic = await ClinicAsync();
        var id = await IssuedAsync(clinic);
        var now = factory.Clock.GetUtcNow();

        var cases = new (object Body, string Field)[]
        {
            (new { amount = 54.01m, method = "Cash" }, "amount"),
            (new { amount = 0, method = "Cash" }, "amount"),
            (new { amount = 1.005m, method = "Cash" }, "amount"),
            (new { amount = 10, method = "Card", reference = "4111 1111 1111 1111" }, "reference"),
            (new { amount = 10, method = "Cash", paidAt = now.AddMinutes(5) }, "paidAt"),
            (new { amount = 10, method = "Cash", paidAt = now.AddDays(-1) }, "paidAt"),
        };
        foreach (var (body, field) in cases)
        {
            var response = await PayAsync(clinic, id, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(field, Assert.Single((await ErrorOf(response)).Details).Field);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PayAsync(clinic, id, new { amount = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PayAsync(clinic, id, new { amount = 10, method = "Crypto" })).StatusCode);

        // A 16-digit reference that fails the Luhn check is not a card number; a backdated payment after the issue is fine.
        factory.Clock.Advance(TimeSpan.FromMinutes(30));
        var ok = await OkAsync(await PayAsync(clinic, id, new { amount = 10, method = "Cheque", reference = "1234567812345678", paidAt = now.AddMinutes(10) }), HttpStatusCode.Created);
        Assert.Equal("1234567812345678", ok.GetProperty("reference").GetString());

        var draft = (await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/invoices", new { clientId = clinic.ClientId }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await PayAsync(clinic, draft, new { amount = 1, method = "Cash" })).StatusCode);
        var voided = await IssuedAsync(clinic);
        await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{voided}/void", new { reason = "x" }));
        Assert.Equal(HttpStatusCode.Conflict, (await PayAsync(clinic, voided, new { amount = 1, method = "Cash" })).StatusCode);
    }

    [Fact]
    public async Task ConcurrentPayments_NeverExceedTheBalance()
    {
        var clinic = await ClinicAsync();
        var id = await IssuedAsync(clinic);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PayAsync(clinic, id, new { amount = 20, method = "Cash" })));

        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
        var invoice = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}");
        Assert.Equal(40m, Amount(invoice, "amountPaid"));
    }

    // ---------- Refunds ----------

    [Fact]
    public async Task Refunds_RaiseTheBalance_UpToThePayment_AndEnableVoiding()
    {
        var clinic = await ClinicAsync();
        var id = await IssuedAsync(clinic);
        var payment = (await OkAsync(await PayAsync(clinic, id, new { amount = 54, method = "Card" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var url = $"/api/invoices/{id}/payments/{payment}/refunds";

        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.PostAsJsonAsync(url, new { amount = 10, method = "Card", reason = "x" })).StatusCode);
        var partial = await OkAsync(await clinic.Admin.PostAsJsonAsync(url, new { amount = 10, method = "Card", reason = " Goodwill " }), HttpStatusCode.Created);

        Assert.Equal(10m, Amount(partial, "refundedAmount"));
        var refund = partial.GetProperty("refunds")[0];
        Assert.Equal(["id", "amount", "method", "reason", "refundedAt", "refundedBy"], refund.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Goodwill", refund.GetProperty("reason").GetString());
        Assert.Equal(("PartiallyPaid", 44m, 10m),
            (partial.GetProperty("invoice").GetProperty("paymentStatus").GetString(), Amount(partial.GetProperty("invoice"), "amountPaid"), Amount(partial.GetProperty("invoice"), "balanceDue")));
        Assert.DoesNotContain("Goodwill", (await factory.AuditEntriesAsync(payment)).Single(e => e.Action == AuditActions.PaymentRefunded).Metadata);

        var tooMuch = await clinic.Admin.PostAsJsonAsync(url, new { amount = 44.01m, method = "Card", reason = "x" });
        Assert.Equal("amount", Assert.Single((await ErrorOf(tooMuch)).Details).Field);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Admin.PostAsJsonAsync(url, new { amount = 1, method = "Card", reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Admin.PostAsJsonAsync($"/api/invoices/{id}/void", new { reason = "x" })).StatusCode);

        await OkAsync(await clinic.Admin.PostAsJsonAsync(url, new { amount = 44, method = "Cash", reason = "Recorded on the wrong invoice" }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Admin.PostAsJsonAsync(url, new { amount = 1, method = "Cash", reason = "x" })).StatusCode);
        var voided = await OkAsync(await clinic.Admin.PostAsJsonAsync($"/api/invoices/{id}/void", new { reason = "Duplicate" }));
        Assert.Equal(JsonValueKind.Null, voided.GetProperty("paymentStatus").ValueKind);
    }

    // ---------- Permissions, tenancy, lists, history ----------

    [Fact]
    public async Task Permissions_AndOtherClinics()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var id = await IssuedAsync(clinic);
        var theirs = await IssuedAsync(other);
        var theirPayment = (await OkAsync(await PayAsync(other, theirs, new { amount = 5, method = "Cash" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var inventory = await factory.SignInAsync(await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Inventory));

        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Vet.PostAsJsonAsync($"/api/invoices/{id}/payments", new { amount = 1, method = "Cash" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clinic.Vet.GetAsync($"/api/invoices/{id}/payments")).StatusCode);
        foreach (var http in new[] { clinic.Nurse, inventory })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/api/invoices/{id}/payments")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await PayAsync(clinic, theirs, new { amount = 1, method = "Cash" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.GetAsync($"/api/invoices/{theirs}/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Admin.PostAsJsonAsync($"/api/invoices/{theirs}/payments/{theirPayment}/refunds", new { amount = 1, method = "Cash", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Admin.PostAsJsonAsync($"/api/invoices/{id}/payments/{theirPayment}/refunds", new { amount = 1, method = "Cash", reason = "x" })).StatusCode);
        var me = await clinic.Desk.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains(Permissions.PaymentsRecord, permissions);
        Assert.DoesNotContain(Permissions.PaymentsRefund, permissions);
    }

    [Fact]
    public async Task InvoiceList_FiltersByPaymentStatus_AndOverdue()
    {
        var clinic = await ClinicAsync();
        var today = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd");
        var unpaid = await IssuedAsync(clinic, today);
        var partial = await IssuedAsync(clinic, today);
        await OkAsync(await PayAsync(clinic, partial, new { amount = 4, method = "Cash" }), HttpStatusCode.Created);
        var paid = await IssuedAsync(clinic, today);
        await OkAsync(await PayAsync(clinic, paid, new { amount = 54, method = "Cash" }), HttpStatusCode.Created);

        var desk = clinic.Desk;
        async Task<List<Guid>> Ids(string query) =>
            (await desk.GetFromJsonAsync<JsonElement>($"/api/invoices?clientId={clinic.ClientId}&{query}"))
                .GetProperty("data").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([unpaid], await Ids("paymentStatus=Unpaid"));
        Assert.Equal([partial], await Ids("paymentStatus=PartiallyPaid"));
        Assert.Equal([paid], await Ids("paymentStatus=Paid"));
        Assert.Empty(await Ids("overdue=true")); // due today is not overdue
        factory.Clock.Advance(TimeSpan.FromDays(2));
        desk = await factory.SignInAsync(await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Receptionist)); // sessions expired
        Assert.Equal(new[] { unpaid, partial }.Order(), (await Ids("overdue=true")).Order());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await desk.GetAsync("/api/invoices?paymentStatus=paid")).StatusCode);
    }

    [Fact]
    public async Task History_ShowsThePaymentState_OnTheInvoiceEntry()
    {
        var clinic = await ClinicAsync();
        var id = await IssuedAsync(clinic);
        await OkAsync(await PayAsync(clinic, id, new { amount = 50, method = "Cash" }), HttpStatusCode.Created);

        var history = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");

        var invoice = history.GetProperty("data").EnumerateArray().Single(e => e.GetProperty("type").GetString() == "InvoiceIssued")
            .GetProperty("details").GetProperty("invoice");
        Assert.Equal(["id", "number", "total", "currency", "status", "paymentStatus", "balanceDue"], invoice.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("PartiallyPaid", 4m), (invoice.GetProperty("paymentStatus").GetString(), invoice.GetProperty("balanceDue").GetDecimal()));
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE payments SET amount = amount + 1 WHERE invoice_id = {0}")]
    [InlineData("DELETE FROM payments WHERE invoice_id = {0}")]
    [InlineData("DELETE FROM refunds WHERE invoice_id = {0}")]
    [InlineData("UPDATE refunds SET amount = 1 WHERE invoice_id = {0}")]
    [InlineData("UPDATE invoices SET amount_paid = 0 WHERE id = {0}")]
    [InlineData("INSERT INTO refunds (id, clinic_id, invoice_id, payment_id, amount, method, reason, refunded_at, refunded_by_user_id, created_at, updated_at) SELECT gen_random_uuid(), clinic_id, invoice_id, id, amount, 'Cash', 'x', now(), received_by_user_id, now(), now() FROM payments WHERE invoice_id = {0}")]
    [InlineData("TRUNCATE payments, refunds")]
    public async Task Database_KeepsTheLedgerImmutable_AndTheAmountPaidExact(string sql)
    {
        var clinic = await ClinicAsync();
        var id = await IssuedAsync(clinic);
        var payment = (await OkAsync(await PayAsync(clinic, id, new { amount = 30, method = "Cash" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Admin.PostAsJsonAsync($"/api/invoices/{id}/payments/{payment}/refunds", new { amount = 5, method = "Cash", reason = "x" }), HttpStatusCode.Created);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Database_RefusesPaymentsOnDrafts_AndVoidingWhileMoneyIsHeld()
    {
        var clinic = await ClinicAsync();
        var draft = (await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/invoices", new { clientId = clinic.ClientId }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var onDraft = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO payments (id, clinic_id, invoice_id, amount, method, paid_at, received_by_user_id, created_at, updated_at) " +
            "SELECT gen_random_uuid(), clinic_id, id, 1, 'Cash', now(), created_by_user_id, now(), now() FROM invoices WHERE id = {0}", draft)));
        Assert.Equal("42501", onDraft.SqlState);

        var id = await IssuedAsync(clinic);
        await OkAsync(await PayAsync(clinic, id, new { amount = 10, method = "Cash" }), HttpStatusCode.Created);
        var voidWithMoney = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE invoices SET status = 'Void', voided_at = now(), voided_by_user_id = issued_by_user_id, void_reason = 'x' WHERE id = {0}", id)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, voidWithMoney.SqlState);
    }

    [Theory]
    [InlineData("4111 1111 1111 1111", true)]
    [InlineData("4111-1111-1111-1111", true)]
    [InlineData("5555555555554444", true)]
    [InlineData("1234567812345678", false)]
    [InlineData("411111111111", false)]
    [InlineData("Slip 0042", false)]
    [InlineData(null, false)]
    public void CardNumbers_AreRecognisedByLengthAndLuhn(string? reference, bool expected) =>
        Assert.Equal(expected, PaymentService.LooksLikeCardNumber(reference));
}
