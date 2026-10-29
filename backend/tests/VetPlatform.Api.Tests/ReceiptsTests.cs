using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Receipts, frozen bill-to blocks and the invoice activity trail (B-18) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class ReceiptsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private sealed record Clinic(
        HttpClient Admin, User AdminUser, HttpClient Vet, HttpClient Nurse, HttpClient Desk, User DeskUser, Guid ClientId, Guid PatientId, Guid Standard);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var vet = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian));
        var nurse = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Nurse));
        var deskUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist);
        var desk = await factory.SignInAsync(deskUser);
        await OkAsync(await admin.PatchAsJsonAsync("/api/clinic", new { currency = "EUR" }));
        var standard = (await OkAsync(await admin.PostAsJsonAsync("/api/tax-rates", new { name = "Standard", ratePercent = 20 }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var client = await desk.PostAsJsonAsync("/api/clients", new { firstName = "Jane", lastName = "Doe", addressLine1 = "1 High Street", city = "Bath", postalCode = "BA1 1AA", country = "UK" });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var patient = await desk.PostAsJsonAsync("/api/patients", new { clientId, name = "Rex", species = "Dog" });
        var patientId = (await patient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return new Clinic(admin, adminUser, vet, nurse, desk, deskUser, clientId, patientId, standard);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>An issued invoice of 54.00 EUR (45 + 20% tax).</summary>
    private static async Task<(Guid Id, JsonElement Issued)> IssuedAsync(Clinic clinic)
    {
        var id = (await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/invoices", new { clientId = clinic.ClientId, patientId = clinic.PatientId }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{id}/items", new { description = "Exam", quantity = 1, unitPrice = 45, taxRateId = clinic.Standard }));
        return (id, await OkAsync(await clinic.Desk.PostAsync($"/api/invoices/{id}/issue", null)));
    }

    private static async Task<JsonElement> PayAsync(Clinic clinic, Guid invoice, decimal amount) =>
        await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/invoices/{invoice}/payments", new { amount, method = "Card", reference = "Slip 7" }), HttpStatusCode.Created);

    // ---------- Bill-to ----------

    [Fact]
    public async Task IssuingFreezesWhoTheInvoiceIsAddressedTo()
    {
        var clinic = await ClinicAsync();
        var draft = (await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/invoices", new { clientId = clinic.ClientId }), HttpStatusCode.Created));
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("billTo").ValueKind);

        var (id, issued) = await IssuedAsync(clinic);
        var billTo = issued.GetProperty("billTo");
        Assert.Equal(
            ["name", "addressLine1", "addressLine2", "city", "region", "postalCode", "country", "patientName"],
            billTo.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("Jane Doe", "1 High Street", "Bath", "Rex"),
            (billTo.GetProperty("name").GetString(), billTo.GetProperty("addressLine1").GetString(), billTo.GetProperty("city").GetString(), billTo.GetProperty("patientName").GetString()));

        // The client moves and is renamed: the issued invoice still reads as printed.
        await OkAsync(await clinic.Desk.PatchAsJsonAsync($"/api/clients/{clinic.ClientId}", new { lastName = "Smith", city = "Leeds" }));
        var read = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}");
        Assert.Equal(billTo.GetRawText(), read.GetProperty("billTo").GetRawText());
        Assert.Equal("Smith", read.GetProperty("client").GetProperty("lastName").GetString()); // the live client summary moves on
    }

    // ---------- Receipts ----------

    [Fact]
    public async Task EachPaymentAndRefund_GetsAGaplessFrozenReceipt()
    {
        var clinic = await ClinicAsync();
        var (id, _) = await IssuedAsync(clinic);

        var payment = await PayAsync(clinic, id, 20);
        var receiptRef = payment.GetProperty("receipt");
        Assert.Equal(1, receiptRef.GetProperty("number").GetInt32());
        var refunded = await OkAsync(await clinic.Admin.PostAsJsonAsync(
            $"/api/invoices/{id}/payments/{payment.GetProperty("id").GetGuid()}/refunds", new { amount = 5, method = "Cash", reason = "Goodwill" }), HttpStatusCode.Created);
        Assert.Equal(2, refunded.GetProperty("refunds")[0].GetProperty("receipt").GetProperty("number").GetInt32());
        Assert.Equal(1, refunded.GetProperty("receipt").GetProperty("number").GetInt32());

        var receipt = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/receipts/{receiptRef.GetProperty("id").GetGuid()}");
        Assert.Equal(
            ["id", "number", "kind", "clinicName", "invoice", "billTo", "paymentId", "refundId", "amount", "method", "reference",
             "occurredAt", "amountPaidAfter", "balanceDueAfter", "issuedBy", "createdAt"],
            receipt.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("Payment", 20m, "Card", "Slip 7"), (receipt.GetProperty("kind").GetString(), receipt.GetProperty("amount").GetDecimal(),
            receipt.GetProperty("method").GetString(), receipt.GetProperty("reference").GetString()));
        Assert.Equal((20m, 34m), (receipt.GetProperty("amountPaidAfter").GetDecimal(), receipt.GetProperty("balanceDueAfter").GetDecimal()));
        Assert.Equal("Jane Doe", receipt.GetProperty("billTo").GetProperty("name").GetString());
        Assert.Equal(clinic.DeskUser.Name, receipt.GetProperty("issuedBy").GetProperty("name").GetString());

        // The cashier is renamed later: the reprint shows the name as printed.
        await OkAsync(await clinic.Admin.PatchAsJsonAsync($"/api/users/{clinic.DeskUser.Id}", new { name = "Renamed Person" }));
        var reprint = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/receipts/{receiptRef.GetProperty("id").GetGuid()}");
        Assert.Equal(receipt.GetRawText(), reprint.GetRawText());
        Assert.Equal(2, (await factory.AuditEntriesAsync(receiptRef.GetProperty("id").GetGuid())).Count(e => e.Action == AuditActions.ReceiptViewed));

        var refundReceipt = await clinic.Desk.GetFromJsonAsync<JsonElement>("/api/receipts?number=2");
        var item = Assert.Single(refundReceipt.GetProperty("data").EnumerateArray());
        Assert.Equal(("Refund", 5m, 15m, 39m), (item.GetProperty("kind").GetString(), item.GetProperty("amount").GetDecimal(),
            item.GetProperty("amountPaidAfter").GetDecimal(), item.GetProperty("balanceDueAfter").GetDecimal()));
        var byInvoice = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/receipts?invoiceId={id}");
        Assert.Equal([2, 1], byInvoice.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("number").GetInt32()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Desk.GetAsync("/api/receipts?number=0")).StatusCode);
    }

    [Fact]
    public async Task ConcurrentPayments_GetDistinctConsecutiveReceiptNumbers()
    {
        var clinic = await ClinicAsync();
        var invoices = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            invoices.Add((await IssuedAsync(clinic)).Id);
        }

        var responses = await Task.WhenAll(invoices.Select(id => clinic.Desk.PostAsJsonAsync($"/api/invoices/{id}/payments", new { amount = 54, method = "Cash" })));

        var numbers = new List<int>();
        foreach (var response in responses)
        {
            numbers.Add((await OkAsync(response, HttpStatusCode.Created)).GetProperty("receipt").GetProperty("number").GetInt32());
        }

        Assert.Equal(Enumerable.Range(1, 6), numbers.Order());
    }

    // ---------- Activity ----------

    [Fact]
    public async Task Activity_TellsHowTheInvoiceReachedItsState()
    {
        var clinic = await ClinicAsync();
        var (id, _) = await IssuedAsync(clinic);
        var (otherInvoice, _) = await IssuedAsync(clinic);
        var payment = await PayAsync(clinic, id, 54);
        await PayAsync(clinic, otherInvoice, 10);
        await OkAsync(await clinic.Admin.PostAsJsonAsync(
            $"/api/invoices/{id}/payments/{payment.GetProperty("id").GetGuid()}/refunds", new { amount = 4, method = "Cash", reason = "Secret reason" }), HttpStatusCode.Created);

        var activity = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/activity");

        var entries = activity.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(
            [AuditActions.InvoiceCreated, AuditActions.InvoiceItemAdded, AuditActions.InvoiceIssued, AuditActions.PaymentRecorded,
             AuditActions.ReceiptIssued, AuditActions.PaymentRefunded, AuditActions.ReceiptIssued],
            entries.Select(e => e.GetProperty("action").GetString()));
        Assert.Equal(["id", "occurredAt", "action", "entityType", "entityId", "actor", "details"], entries[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(clinic.DeskUser.Id, entries[0].GetProperty("actor").GetProperty("id").GetGuid());
        Assert.Equal(54m, entries[3].GetProperty("details").GetProperty("amount").GetDecimal());
        Assert.DoesNotContain("Secret", activity.GetRawText());
        Assert.DoesNotContain("Slip", activity.GetRawText());
    }

    // ---------- Permissions and tenancy ----------

    [Fact]
    public async Task Permissions_AndOtherClinics()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var (id, _) = await IssuedAsync(clinic);
        var receipt = (await PayAsync(clinic, id, 10)).GetProperty("receipt").GetProperty("id").GetGuid();
        var (theirInvoice, _) = await IssuedAsync(other);
        var theirReceipt = (await PayAsync(other, theirInvoice, 10)).GetProperty("receipt").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Nurse.GetAsync($"/api/receipts/{receipt}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Nurse.GetAsync($"/api/invoices/{id}/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clinic.Vet.GetAsync($"/api/invoices/{id}/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.GetAsync($"/api/receipts/{theirReceipt}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.GetAsync($"/api/invoices/{theirInvoice}/activity")).StatusCode);
        var list = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/receipts?invoiceId={theirInvoice}");
        Assert.Equal(0, list.GetProperty("pagination").GetProperty("total").GetInt32());
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE receipts SET amount = amount + 1 WHERE invoice_id = {0}")]
    [InlineData("UPDATE receipts SET clinic_name = 'Other' WHERE invoice_id = {0}")]
    [InlineData("DELETE FROM receipts WHERE invoice_id = {0}")]
    [InlineData("TRUNCATE receipts")]
    [InlineData("UPDATE invoices SET bill_to_name = 'Someone else' WHERE id = {0}")]
    [InlineData("INSERT INTO receipts (id, clinic_id, number, kind, invoice_id, payment_id, clinic_name, invoice_number, currency, invoice_total, amount, method, occurred_at, amount_paid_after, balance_due_after, issued_by_user_id, issued_by_name, created_at, updated_at) SELECT gen_random_uuid(), clinic_id, 999, 'Payment', invoice_id, payment_id, clinic_name, invoice_number, currency, invoice_total, amount + 1, method, occurred_at, amount_paid_after, balance_due_after, issued_by_user_id, issued_by_name, now(), now() FROM receipts WHERE invoice_id = {0}")]
    public async Task Database_KeepsReceiptsAndBillToFrozen(string sql)
    {
        var clinic = await ClinicAsync();
        var (id, _) = await IssuedAsync(clinic);
        await PayAsync(clinic, id, 10);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal("42501", exception.SqlState);
    }
}
