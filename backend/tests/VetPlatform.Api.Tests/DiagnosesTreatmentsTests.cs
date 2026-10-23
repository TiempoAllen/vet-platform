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

/// <summary>Diagnoses, treatments and the patient problem list (B-12) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class DiagnosesTreatmentsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
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

    private static async Task<JsonElement> AddDiagnosisAsync(HttpClient http, Guid consultation, object body) =>
        await OkAsync(await http.PostAsJsonAsync($"/api/consultations/{consultation}/diagnoses", body));

    private static Guid Last(JsonElement consultation, string collection, string name) =>
        consultation.GetProperty(collection).EnumerateArray().Single(e => e.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    // ---------- While in progress ----------

    [Fact]
    public async Task DiagnosesAndTreatments_AreRecordedOnTheConsultation()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);

        var withDiagnosis = await AddDiagnosisAsync(clinic.Vet, id, new
        {
            name = " Otitis externa ", code = "OTEX", codeSystem = "VeNom", isPrimary = true, notes = "Left ear",
        });
        var diagnosis = withDiagnosis.GetProperty("diagnoses")[0];
        Assert.Equal("Otitis externa", diagnosis.GetProperty("name").GetString());
        Assert.Equal("Provisional", diagnosis.GetProperty("status").GetString());
        Assert.True(diagnosis.GetProperty("isPrimary").GetBoolean());
        Assert.Equal(
            ["id", "name", "code", "codeSystem", "status", "isPrimary", "notes", "createdAt", "updatedAt"],
            diagnosis.EnumerateObject().Select(p => p.Name));

        var withTreatment = await OkAsync(await clinic.Nurse.PostAsJsonAsync($"/api/consultations/{id}/treatments", new
        {
            name = "Ear flush", type = "Procedure", diagnosisId = diagnosis.GetProperty("id").GetGuid(), notes = "Under sedation",
        }));
        var treatment = withTreatment.GetProperty("treatments")[0];
        Assert.Equal(diagnosis.GetProperty("id").GetGuid(), treatment.GetProperty("diagnosisId").GetGuid());
        Assert.Equal(
            ["id", "name", "type", "diagnosisId", "dose", "route", "frequency", "duration", "notes", "createdAt", "updatedAt"],
            treatment.EnumerateObject().Select(p => p.Name));

        var read = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/consultations/{id}");
        Assert.Equal(1, read.GetProperty("diagnoses").GetArrayLength());
        Assert.Equal(1, read.GetProperty("treatments").GetArrayLength());
        Assert.Contains(AuditActions.DiagnosisAdded, (await factory.AuditEntriesAsync(id)).Select(e => e.Action));
    }

    [Fact]
    public async Task MakingADiagnosisPrimary_ClearsTheOtherPrimary()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        await AddDiagnosisAsync(clinic.Vet, id, new { name = "First", isPrimary = true });
        var second = Last(await AddDiagnosisAsync(clinic.Vet, id, new { name = "Second" }), "diagnoses", "Second");

        var updated = await OkAsync(await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{id}/diagnoses/{second}", new { isPrimary = true }));

        var diagnoses = updated.GetProperty("diagnoses").EnumerateArray().ToList();
        Assert.Equal(["Second", "First"], diagnoses.Select(d => d.GetProperty("name").GetString())); // primary first
        Assert.Equal([true, false], diagnoses.Select(d => d.GetProperty("isPrimary").GetBoolean()));

        var addedPrimary = await AddDiagnosisAsync(clinic.Vet, id, new { name = "Third", isPrimary = true });
        Assert.Single(addedPrimary.GetProperty("diagnoses").EnumerateArray(), d => d.GetProperty("isPrimary").GetBoolean());
    }

    [Fact]
    public async Task RemovingADiagnosis_UnlinksItsTreatments()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        var diagnosis = Last(await AddDiagnosisAsync(clinic.Vet, id, new { name = "Wound" }), "diagnoses", "Wound");
        var withTreatment = await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/treatments", new { name = "Suture", type = "Procedure", diagnosisId = diagnosis }));
        var treatment = withTreatment.GetProperty("treatments")[0].GetProperty("id").GetGuid();

        var removed = await OkAsync(await clinic.Vet.DeleteAsync($"/api/consultations/{id}/diagnoses/{diagnosis}"));

        Assert.Empty(removed.GetProperty("diagnoses").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("treatments")[0].GetProperty("diagnosisId").ValueKind);

        var edited = await OkAsync(await clinic.Vet.PatchAsync($"/api/consultations/{id}/treatments/{treatment}", Json("""{ "dose": "3 sutures", "notes": "" }""")));
        Assert.Equal("3 sutures", edited.GetProperty("treatments")[0].GetProperty("dose").GetString());
        var gone = await OkAsync(await clinic.Vet.DeleteAsync($"/api/consultations/{id}/treatments/{treatment}"));
        Assert.Empty(gone.GetProperty("treatments").EnumerateArray());
    }

    [Theory]
    [InlineData("diagnoses", """{ "name": "  " }""", HttpStatusCode.UnprocessableEntity)]
    [InlineData("diagnoses", """{ "name": "X", "codeSystem": "VeNom" }""", HttpStatusCode.UnprocessableEntity)]
    [InlineData("diagnoses", """{ "name": "X", "status": "Suspected" }""", HttpStatusCode.BadRequest)]
    [InlineData("treatments", """{ "name": "Flush" }""", HttpStatusCode.UnprocessableEntity)]
    [InlineData("treatments", """{ "name": "Flush", "type": "Surgery" }""", HttpStatusCode.BadRequest)]
    [InlineData("treatments", """{ "name": "Flush", "type": "Procedure", "diagnosisId": "00000000-0000-0000-0000-000000000001" }""", HttpStatusCode.UnprocessableEntity)]
    public async Task InvalidItems_AreRejected(string collection, string json, HttpStatusCode expected)
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);

        var response = await clinic.Vet.PostAsync($"/api/consultations/{id}/{collection}", Json(json));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ItemsOfAnotherConsultation_AreNotFound_AndCannotBeLinked()
    {
        var clinic = await ClinicAsync();
        var first = await OpenAsync(clinic);
        var diagnosis = Last(await AddDiagnosisAsync(clinic.Vet, first, new { name = "Elsewhere" }), "diagnoses", "Elsewhere");
        await OkAsync(await clinic.Vet.PostAsync($"/api/consultations/{first}/complete", null));
        var second = await OpenAsync(clinic);

        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{second}/diagnoses/{diagnosis}", new { name = "x" })).StatusCode);
        var link = await clinic.Vet.PostAsJsonAsync($"/api/consultations/{second}/treatments", new { name = "x", type = "Other", diagnosisId = diagnosis });
        Assert.Equal("diagnosisId", Assert.Single((await ErrorOf(link)).Details).Field);
    }

    [Fact]
    public async Task Permissions_FollowTheClinicalRoles()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);

        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Admin.PostAsJsonAsync($"/api/consultations/{id}/diagnoses", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.PostAsJsonAsync($"/api/consultations/{id}/treatments", new { name = "x", type = "Other" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.GetAsync($"/api/patients/{clinic.PatientId}/diagnoses")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clinic.Admin.GetAsync($"/api/patients/{clinic.PatientId}/diagnoses")).StatusCode);
    }

    // ---------- After completion ----------

    [Fact]
    public async Task AfterCompletion_OnlyAVetCanChangeADiagnosisStatus()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        var diagnosis = Last(await AddDiagnosisAsync(clinic.Vet, id, new { name = "Kidney disease" }), "diagnoses", "Kidney disease");
        var withTreatment = await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/treatments", new { name = "Fluids", type = "Fluids" }));
        var treatment = withTreatment.GetProperty("treatments")[0].GetProperty("id").GetGuid();
        await OkAsync(await clinic.Vet.PostAsync($"/api/consultations/{id}/complete", null));

        var frozen = new[]
        {
            await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/diagnoses", new { name = "Late" }),
            await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{id}/diagnoses/{diagnosis}", new { name = "Renamed" }),
            await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{id}/diagnoses/{diagnosis}", new { status = "Confirmed", notes = "x" }),
            await clinic.Vet.DeleteAsync($"/api/consultations/{id}/diagnoses/{diagnosis}"),
            await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/treatments", new { name = "Late", type = "Other" }),
            await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{id}/treatments/{treatment}", new { dose = "x" }),
            await clinic.Vet.DeleteAsync($"/api/consultations/{id}/treatments/{treatment}"),
        };
        Assert.All(frozen, response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));

        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Nurse.PatchAsJsonAsync($"/api/consultations/{id}/diagnoses/{diagnosis}", new { status = "Confirmed" })).StatusCode);

        var confirmed = await OkAsync(await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{id}/diagnoses/{diagnosis}", new { status = "Confirmed" }));
        Assert.Equal("Confirmed", confirmed.GetProperty("diagnoses")[0].GetProperty("status").GetString());
        var entry = (await factory.AuditEntriesAsync(id)).Last();
        Assert.Equal(AuditActions.DiagnosisUpdated, entry.Action);
        Assert.Contains("afterCompletion", entry.Metadata);
        Assert.DoesNotContain("Kidney", entry.Metadata);
    }

    [Fact]
    public async Task DiscardedConsultations_FreezeTheirDiagnosesCompletely()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        var diagnosis = Last(await AddDiagnosisAsync(clinic.Vet, id, new { name = "Mistake" }), "diagnoses", "Mistake");
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/discard", new { reason = "Wrong patient" }));

        var response = await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{id}/diagnoses/{diagnosis}", new { status = "RuledOut" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---------- Problem list and history ----------

    [Fact]
    public async Task ProblemList_ShowsDiagnosesOfCompletedConsultations_AndIsAudited()
    {
        var clinic = await ClinicAsync();
        var done = await OpenAsync(clinic);
        await AddDiagnosisAsync(clinic.Vet, done, new { name = "Otitis", status = "Confirmed", isPrimary = true });
        await AddDiagnosisAsync(clinic.Vet, done, new { name = "Allergy" });
        await OkAsync(await clinic.Vet.PostAsync($"/api/consultations/{done}/complete", null));
        var open = await OpenAsync(clinic);
        await AddDiagnosisAsync(clinic.Vet, open, new { name = "Not signed yet" });

        var list = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/diagnoses");
        var confirmed = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/diagnoses?status=Confirmed");

        Assert.Equal(["Otitis", "Allergy"], list.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("diagnosis").GetProperty("name").GetString()));
        var first = list.GetProperty("data")[0];
        Assert.Equal(["diagnosis", "consultationId", "consultationCompletedAt", "veterinarian"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(done, first.GetProperty("consultationId").GetGuid());
        Assert.Equal(1, confirmed.GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.Contains(AuditActions.DiagnosesListed, (await factory.AuditEntriesAsync(clinic.PatientId)).Select(e => e.Action));

        var badStatus = await clinic.Vet.GetAsync($"/api/patients/{clinic.PatientId}/diagnoses?status=confirmed");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badStatus.StatusCode);
    }

    [Fact]
    public async Task History_ShowsTheConsultationsDiagnoses()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        await AddDiagnosisAsync(clinic.Vet, id, new { name = "Otitis", isPrimary = true });
        await OkAsync(await clinic.Vet.PostAsync($"/api/consultations/{id}/complete", null));

        var history = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");

        var diagnosis = history.GetProperty("data")[0].GetProperty("details").GetProperty("consultation").GetProperty("diagnoses")[0];
        Assert.Equal(["id", "name", "status", "isPrimary"], diagnosis.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Otitis", diagnosis.GetProperty("name").GetString());
    }

    [Fact]
    public async Task OtherClinics_AreNotFound()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var theirs = await OpenAsync(other);

        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.PostAsJsonAsync($"/api/consultations/{theirs}/diagnoses", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.PostAsJsonAsync($"/api/consultations/{theirs}/treatments", new { name = "x", type = "Other" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/patients/{other.PatientId}/diagnoses")).StatusCode);
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE diagnoses SET name = 'rewritten' WHERE consultation_id = {0}", "42501")]
    [InlineData("DELETE FROM diagnoses WHERE consultation_id = {0}", "42501")]
    [InlineData("UPDATE treatments SET dose = 'more' WHERE consultation_id = {0}", "42501")]
    [InlineData("INSERT INTO diagnoses (id, clinic_id, consultation_id, patient_id, name, status, is_primary, created_at, updated_at) SELECT gen_random_uuid(), clinic_id, id, patient_id, 'late', 'Provisional', false, now(), now() FROM consultations WHERE id = {0}", "42501")]
    public async Task Database_FreezesItemsOfCompletedConsultations(string sql, string sqlState)
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        await AddDiagnosisAsync(clinic.Vet, id, new { name = "Otitis" });
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{id}/treatments", new { name = "Flush", type = "Procedure" }));
        await OkAsync(await clinic.Vet.PostAsync($"/api/consultations/{id}/complete", null));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal(sqlState, exception.SqlState);
        // A status change alone is still allowed by the database.
        await factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE diagnoses SET status = 'Confirmed' WHERE consultation_id = {0}", id));
    }

    [Fact]
    public async Task Database_KeepsItemsOnTheirConsultationsPatient()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic);
        await AddDiagnosisAsync(clinic.Vet, id, new { name = "Otitis" });
        var client = await clinic.Desk.PostAsJsonAsync("/api/clients", new { firstName = "John", lastName = "Smith" });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var patient = await clinic.Desk.PostAsJsonAsync("/api/patients", new { clientId, name = "Tom", species = "Cat" });
        var otherPatient = (await patient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE diagnoses SET patient_id = {0} WHERE consultation_id = {1}", otherPatient, id)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }
}
