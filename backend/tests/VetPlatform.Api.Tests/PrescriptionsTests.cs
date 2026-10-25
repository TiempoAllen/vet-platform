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

/// <summary>Prescriptions and the patient medication list (B-13) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class PrescriptionsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed record Clinic(HttpClient Admin, User AdminUser, HttpClient Vet, User VetUser, HttpClient Nurse, HttpClient Desk, Guid PatientId);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var vetUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian);
        var nurse = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Nurse));
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        var client = await desk.PostAsJsonAsync("/api/clients", new { firstName = "Jane", lastName = "Doe" });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var patient = await desk.PostAsJsonAsync("/api/patients", new { clientId, name = "Rex", species = "Dog" });
        var patientId = (await patient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return new Clinic(admin, adminUser, await factory.SignInAsync(vetUser), vetUser, nurse, desk, patientId);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> OpenAsync(Clinic clinic) =>
        (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new
        {
            patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id, assessment = "Ear infection",
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static object Amoxicillin(Guid? diagnosisId = null, string name = "Amoxicillin 250 mg") => new
    {
        medicationName = name, dose = "1 tablet", route = "Oral", frequency = "Twice daily", duration = "7 days",
        quantity = 14, quantityUnit = "tablets", instructions = "Give with food", diagnosisId,
    };

    private static async Task<JsonElement> AddAsync(Clinic clinic, Guid consultation, object body) =>
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{consultation}/prescriptions", body));

    private static Guid Named(JsonElement consultation, string name) =>
        consultation.GetProperty("prescriptions").EnumerateArray()
            .Single(p => p.GetProperty("medicationName").GetString() == name).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> CompleteAsync(Clinic clinic, Guid consultation) =>
        clinic.Vet.PostAsync($"/api/consultations/{consultation}/complete", null);

    // ---------- While in progress ----------

    [Fact]
    public async Task AVet_PrescribesOnTheConsultation()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);

        var consultation = await AddAsync(clinic, id, new
        {
            medicationName = " Meloxicam 1.5 mg/ml ", dose = "0.5 ml", frequency = "Once daily", quantity = 10.5, quantityUnit = "ml",
            route = "  ", refills = 2,
        });

        var prescription = consultation.GetProperty("prescriptions")[0];
        Assert.Equal(
            ["id", "medicationName", "dose", "route", "frequency", "duration", "quantity", "quantityUnit", "refills", "instructions",
             "diagnosisId", "status", "prescribedBy", "cancelledAt", "cancelledBy", "cancelReason", "createdAt", "updatedAt"],
            prescription.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Meloxicam 1.5 mg/ml", prescription.GetProperty("medicationName").GetString());
        Assert.Equal(JsonValueKind.Null, prescription.GetProperty("route").ValueKind);
        Assert.Equal("10.5", prescription.GetProperty("quantity").GetRawText());
        Assert.Equal(2, prescription.GetProperty("refills").GetInt32());
        Assert.Equal("Active", prescription.GetProperty("status").GetString());
        Assert.Equal(clinic.VetUser.Id, prescription.GetProperty("prescribedBy").GetProperty("id").GetGuid());

        var read = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/consultations/{id}");
        Assert.Equal(prescription.GetRawText(), read.GetProperty("prescriptions")[0].GetRawText()); // response equals a later read
        var entry = (await factory.AuditEntriesAsync(id)).Last(e => e.Action == AuditActions.PrescriptionAdded);
        Assert.DoesNotContain("Meloxicam", entry.Metadata);
    }

    [Fact]
    public async Task OnlyVets_WritePrescriptions_AndClinicalRolesReadThem()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        var prescription = Named(await AddAsync(clinic, id, Amoxicillin()), "Amoxicillin 250 mg");
        var inventory = await factory.SignInAsync(await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Inventory));

        foreach (var http in new[] { clinic.Nurse, clinic.Admin, clinic.Desk, inventory })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync($"/api/consultations/{id}/prescriptions", Amoxicillin())).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PatchAsJsonAsync($"/api/consultations/{id}/prescriptions/{prescription}", new { dose = "2" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.DeleteAsync($"/api/consultations/{id}/prescriptions/{prescription}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync($"/api/consultations/{id}/prescriptions/{prescription}/cancel", new { reason = "x" })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await clinic.Nurse.GetAsync($"/api/patients/{clinic.PatientId}/prescriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clinic.Admin.GetAsync($"/api/patients/{clinic.PatientId}/prescriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.GetAsync($"/api/patients/{clinic.PatientId}/prescriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inventory.GetAsync($"/api/patients/{clinic.PatientId}/prescriptions")).StatusCode);

        // The nurse still sees it in the consultation (clinical.read).
        var read = await clinic.Nurse.GetFromJsonAsync<JsonElement>($"/api/consultations/{id}");
        Assert.Equal(1, read.GetProperty("prescriptions").GetArrayLength());
    }

    [Theory]
    [InlineData("""{ "dose": "1", "frequency": "daily", "quantity": 1, "quantityUnit": "tab" }""", "MedicationName")]
    [InlineData("""{ "medicationName": "  ", "dose": "1", "frequency": "daily", "quantity": 1, "quantityUnit": "tab" }""", "MedicationName")]
    [InlineData("""{ "medicationName": "X", "frequency": "daily", "quantity": 1, "quantityUnit": "tab" }""", "Dose")]
    [InlineData("""{ "medicationName": "X", "dose": "1", "quantity": 1, "quantityUnit": "tab" }""", "Frequency")]
    [InlineData("""{ "medicationName": "X", "dose": "1", "frequency": "daily", "quantityUnit": "tab" }""", "Quantity")]
    [InlineData("""{ "medicationName": "X", "dose": "1", "frequency": "daily", "quantity": 1 }""", "QuantityUnit")]
    [InlineData("""{ "medicationName": "X", "dose": "1", "frequency": "daily", "quantity": 0, "quantityUnit": "tab" }""", "quantity")]
    [InlineData("""{ "medicationName": "X", "dose": "1", "frequency": "daily", "quantity": 1.2345, "quantityUnit": "tab" }""", "quantity")]
    [InlineData("""{ "medicationName": "X", "dose": "1", "frequency": "daily", "quantity": 100001, "quantityUnit": "tab" }""", "quantity")]
    [InlineData("""{ "medicationName": "X", "dose": "1", "frequency": "daily", "quantity": 1, "quantityUnit": "tab", "refills": 13 }""", "refills")]
    [InlineData("""{ "medicationName": "X", "dose": "1", "frequency": "daily", "quantity": 1, "quantityUnit": "tab", "refills": -1 }""", "refills")]
    [InlineData("""{ "medicationName": "X", "dose": "1", "frequency": "daily", "quantity": 1, "quantityUnit": "tab", "diagnosisId": "00000000-0000-0000-0000-000000000001" }""", "diagnosisId")]
    public async Task InvalidPrescriptions_Are422(string json, string field)
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);

        var response = await clinic.Vet.PostAsync($"/api/consultations/{id}/prescriptions", Json(json));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, d => string.Equals(d.Field, field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Edits_ChangeOnlyWhatIsSent_AndRemovingDeletes()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        var diagnosis = (await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/diagnoses", new { name = "Otitis" })))
            .GetProperty("diagnoses")[0].GetProperty("id").GetGuid();
        var prescription = Named(await AddAsync(clinic, id, Amoxicillin(diagnosis)), "Amoxicillin 250 mg");

        var edited = await OkAsync(await clinic.Vet.PatchAsync(
            $"/api/consultations/{id}/prescriptions/{prescription}",
            Json("""{ "quantity": 28, "refills": 1, "instructions": "", "diagnosisId": null }""")));

        var item = edited.GetProperty("prescriptions")[0];
        Assert.Equal("28", item.GetProperty("quantity").GetRawText());
        Assert.Equal(1, item.GetProperty("refills").GetInt32());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("instructions").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("diagnosisId").ValueKind);
        Assert.Equal("Twice daily", item.GetProperty("frequency").GetString());
        var entry = (await factory.AuditEntriesAsync(id)).Last();
        Assert.Equal(AuditActions.PrescriptionUpdated, entry.Action);
        Assert.Contains("quantity", entry.Metadata);

        var blank = await clinic.Vet.PatchAsync($"/api/consultations/{id}/prescriptions/{prescription}", Json("""{ "dose": " " }"""));
        Assert.Equal("dose", Assert.Single((await ErrorOf(blank)).Details).Field);
        var badLink = await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{id}/prescriptions/{prescription}", new { diagnosisId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badLink.StatusCode);

        var removed = await OkAsync(await clinic.Vet.DeleteAsync($"/api/consultations/{id}/prescriptions/{prescription}"));
        Assert.Empty(removed.GetProperty("prescriptions").EnumerateArray());
    }

    [Fact]
    public async Task RemovingADiagnosis_UnlinksItsPrescriptions()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        var diagnosis = (await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/diagnoses", new { name = "Wound" })))
            .GetProperty("diagnoses")[0].GetProperty("id").GetGuid();
        var prescription = Named(await AddAsync(clinic, id, Amoxicillin(diagnosis)), "Amoxicillin 250 mg");

        var removed = await OkAsync(await clinic.Vet.DeleteAsync($"/api/consultations/{id}/diagnoses/{diagnosis}"));

        Assert.Equal(JsonValueKind.Null, removed.GetProperty("prescriptions")[0].GetProperty("diagnosisId").ValueKind);
        var entry = (await factory.AuditEntriesAsync(id)).Last();
        Assert.Equal(AuditActions.DiagnosisRemoved, entry.Action);
        Assert.Contains(prescription.ToString(), entry.Metadata);
    }

    [Fact]
    public async Task TheCap_Is409()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        await factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO prescriptions (id, clinic_id, consultation_id, patient_id, medication_name, dose, frequency, quantity, quantity_unit, refills, status, prescribed_by_user_id, created_at, updated_at) " +
            "SELECT gen_random_uuid(), clinic_id, id, patient_id, 'P' || n, '1', 'daily', 1, 'tab', 0, 'Active', veterinarian_id, now(), now() FROM consultations, generate_series(1, 50) n WHERE id = {0}", id));

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/prescriptions", Amoxicillin())).StatusCode);
    }

    // ---------- After completion ----------

    [Fact]
    public async Task AfterCompletion_APrescriptionIsFrozen_ButAVetCanCancelIt()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        var prescription = Named(await AddAsync(clinic, id, Amoxicillin()), "Amoxicillin 250 mg");
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/prescriptions/{prescription}/cancel", new { reason = "x" })).StatusCode);
        await OkAsync(await CompleteAsync(clinic, id));

        var frozen = new[]
        {
            await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/prescriptions", Amoxicillin(name: "Late")),
            await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{id}/prescriptions/{prescription}", new { dose = "2 tablets" }),
            await clinic.Vet.DeleteAsync($"/api/consultations/{id}/prescriptions/{prescription}"),
        };
        Assert.All(frozen, response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/prescriptions/{prescription}/cancel", new { reason = " " })).StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        var cancelled = await OkAsync(await clinic.Vet.PostAsJsonAsync(
            $"/api/consultations/{id}/prescriptions/{prescription}/cancel", new { reason = " Vomiting after first dose " }));

        var item = cancelled.GetProperty("prescriptions")[0];
        Assert.Equal("Cancelled", item.GetProperty("status").GetString());
        Assert.Equal("Vomiting after first dose", item.GetProperty("cancelReason").GetString());
        Assert.Equal(clinic.VetUser.Id, item.GetProperty("cancelledBy").GetProperty("id").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("cancelledAt").ValueKind);
        var read = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/consultations/{id}");
        Assert.Equal(item.GetRawText(), read.GetProperty("prescriptions")[0].GetRawText());

        var entry = (await factory.AuditEntriesAsync(id)).Last(e => e.Action != AuditActions.ConsultationViewed);
        Assert.Equal(AuditActions.PrescriptionCancelled, entry.Action);
        Assert.DoesNotContain("Vomiting", entry.Metadata);
        Assert.DoesNotContain("Amoxicillin", entry.Metadata);

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/prescriptions/{prescription}/cancel", new { reason = "again" })).StatusCode);
    }

    [Fact]
    public async Task DiscardedConsultations_FreezeTheirPrescriptionsCompletely()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        var prescription = Named(await AddAsync(clinic, id, Amoxicillin()), "Amoxicillin 250 mg");
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/discard", new { reason = "Wrong patient" }));

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{id}/prescriptions/{prescription}", new { dose = "2" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Vet.DeleteAsync($"/api/consultations/{id}/prescriptions/{prescription}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/prescriptions/{prescription}/cancel", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task UnknownAndForeignPrescriptions_Are404()
    {
        var clinic = await ClinicAsync();
        var first = await OpenAsync(clinic);
        var elsewhere = Named(await AddAsync(clinic, first, Amoxicillin()), "Amoxicillin 250 mg");
        await OkAsync(await CompleteAsync(clinic, first));
        var second = await OpenAsync(clinic);

        foreach (var prescription in new[] { Guid.NewGuid(), elsewhere })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{second}/prescriptions/{prescription}", new { dose = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.DeleteAsync($"/api/consultations/{second}/prescriptions/{prescription}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.PostAsJsonAsync($"/api/consultations/{first}/prescriptions/{Guid.NewGuid()}/cancel", new { reason = "x" })).StatusCode);
        }

        var other = await ClinicAsync();
        var theirs = await OpenAsync(other);
        var theirPrescription = Named(await AddAsync(other, theirs, Amoxicillin()), "Amoxicillin 250 mg");
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.PostAsJsonAsync($"/api/consultations/{theirs}/prescriptions", Amoxicillin())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{second}/prescriptions/{theirPrescription}", new { dose = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/patients/{other.PatientId}/prescriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/patients/{Guid.NewGuid()}/prescriptions")).StatusCode);
    }

    // ---------- Medication list and history ----------

    [Fact]
    public async Task MedicationList_ShowsPrescriptionsOfCompletedConsultations_NewestFirst_AndIsAudited()
    {
        var clinic = await ClinicAsync();
        var older = await OpenAsync(clinic);
        await AddAsync(clinic, older, Amoxicillin(name: "Older"));
        await OkAsync(await CompleteAsync(clinic, older));
        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        var newer = await OpenAsync(clinic);
        await AddAsync(clinic, newer, Amoxicillin(name: "Newer 1"));
        var stopped = Named(await AddAsync(clinic, newer, Amoxicillin(name: "Newer 2")), "Newer 2");
        await OkAsync(await CompleteAsync(clinic, newer));
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{newer}/prescriptions/{stopped}/cancel", new { reason = "Stopped" }));
        var open = await OpenAsync(clinic);
        await AddAsync(clinic, open, Amoxicillin(name: "Not signed yet"));

        var list = await clinic.Nurse.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/prescriptions");
        var active = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/prescriptions?status=Active");

        Assert.Equal(
            ["Newer 1", "Newer 2", "Older"],
            list.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("prescription").GetProperty("medicationName").GetString()));
        var first = list.GetProperty("data")[0];
        Assert.Equal(["prescription", "consultationId", "consultationCompletedAt", "veterinarian"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(newer, first.GetProperty("consultationId").GetGuid());
        Assert.Equal(clinic.VetUser.Id, first.GetProperty("prescription").GetProperty("prescribedBy").GetProperty("id").GetGuid());
        Assert.Equal("Cancelled", list.GetProperty("data")[1].GetProperty("prescription").GetProperty("status").GetString());
        Assert.Equal(clinic.VetUser.Id, list.GetProperty("data")[1].GetProperty("prescription").GetProperty("cancelledBy").GetProperty("id").GetGuid());
        Assert.Equal(2, active.GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.Contains(AuditActions.PrescriptionsListed, (await factory.AuditEntriesAsync(clinic.PatientId)).Select(e => e.Action));

        var badStatus = await clinic.Vet.GetAsync($"/api/patients/{clinic.PatientId}/prescriptions?status=active");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badStatus.StatusCode);
    }

    [Fact]
    public async Task History_ShowsTheConsultationsPrescriptions_WithTheirCurrentStatus()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        var prescription = Named(await AddAsync(clinic, id, Amoxicillin()), "Amoxicillin 250 mg");
        await OkAsync(await CompleteAsync(clinic, id));
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/prescriptions/{prescription}/cancel", new { reason = "Stopped" }));

        var history = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");

        var item = history.GetProperty("data")[0].GetProperty("details").GetProperty("consultation").GetProperty("prescriptions")[0];
        Assert.Equal(["id", "medicationName", "status"], item.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Cancelled", item.GetProperty("status").GetString());
    }

    // ---------- Database rules ----------

    private const string InsertOne =
        "INSERT INTO prescriptions (id, clinic_id, consultation_id, patient_id, medication_name, dose, frequency, quantity, quantity_unit, refills, status, prescribed_by_user_id, created_at, updated_at) " +
        "SELECT gen_random_uuid(), clinic_id, id, patient_id, 'late', '1', 'daily', 1, 'tab', 0, '{1}', veterinarian_id, now(), now() FROM consultations WHERE id = {0}";

    [Theory]
    [InlineData("UPDATE prescriptions SET dose = 'more' WHERE consultation_id = {0}")]
    [InlineData("DELETE FROM prescriptions WHERE consultation_id = {0}")]
    [InlineData("UPDATE prescriptions SET status = 'Cancelled', cancelled_at = now(), cancelled_by_user_id = prescribed_by_user_id, cancel_reason = 'x', dose = 'more' WHERE consultation_id = {0}")]
    [InlineData("UPDATE prescriptions SET id = gen_random_uuid() WHERE consultation_id = {0}")]
    [InlineData("TRUNCATE prescriptions")]
    public async Task Database_FreezesPrescriptionsOfCompletedConsultations(string sql)
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        await AddAsync(clinic, id, Amoxicillin());
        await OkAsync(await CompleteAsync(clinic, id));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));
        Assert.Equal("42501", exception.SqlState);
        var insert = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync(InsertOne.Replace("{1}", "Active"), id)));
        Assert.Equal("42501", insert.SqlState);

        // The cancellation step alone is allowed, and it is one-way.
        await factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE prescriptions SET status = 'Cancelled', cancelled_at = now(), cancelled_by_user_id = prescribed_by_user_id, cancel_reason = 'x' WHERE consultation_id = {0}", id));
        var undo = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE prescriptions SET status = 'Active', cancelled_at = NULL, cancelled_by_user_id = NULL, cancel_reason = NULL WHERE consultation_id = {0}", id)));
        Assert.Equal("42501", undo.SqlState);
    }

    [Fact]
    public async Task Database_RefusesCancelledPrescriptionsOnOpenConsultations_AndInconsistentCancellations()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        await AddAsync(clinic, id, Amoxicillin());

        var cancelledWhileOpen = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE prescriptions SET status = 'Cancelled', cancelled_at = now(), cancelled_by_user_id = prescribed_by_user_id, cancel_reason = 'x' WHERE consultation_id = {0}", id)));
        Assert.Equal("42501", cancelledWhileOpen.SqlState);

        var noReason = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE prescriptions SET cancel_reason = 'x' WHERE consultation_id = {0}", id)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noReason.SqlState);
    }

    [Fact]
    public async Task Database_KeepsPrescriptionsOnTheirConsultationsPatient()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        await AddAsync(clinic, id, Amoxicillin());
        var client = await clinic.Desk.PostAsJsonAsync("/api/clients", new { firstName = "John", lastName = "Smith" });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var patient = await clinic.Desk.PostAsJsonAsync("/api/patients", new { clientId, name = "Tom", species = "Cat" });
        var otherPatient = (await patient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE prescriptions SET patient_id = {0} WHERE consultation_id = {1}", otherPatient, id)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }
}
