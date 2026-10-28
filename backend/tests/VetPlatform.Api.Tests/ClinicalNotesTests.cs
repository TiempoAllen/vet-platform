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

/// <summary>Append-only clinical notes on patients (B-15) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class ClinicalNotesTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private sealed record Clinic(HttpClient Admin, User AdminUser, HttpClient Vet, User VetUser, HttpClient Nurse, HttpClient Desk, Guid ClientId, Guid PatientId);

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
        return new Clinic(admin, adminUser, await factory.SignInAsync(vetUser), vetUser, nurse, desk, clientId, patientId);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> OpenConsultationAsync(Clinic clinic, Guid? patientId = null) =>
        (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = patientId ?? clinic.PatientId, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> AddAsync(HttpClient http, Guid patientId, object body) =>
        http.PostAsJsonAsync($"/api/patients/{patientId}/clinical-notes", body);

    [Fact]
    public async Task ANurse_AddsANote_ThatReadsBackAndIsAudited()
    {
        var clinic = await ClinicAsync();

        var response = await AddAsync(clinic.Nurse, clinic.PatientId, new { text = " Owner called: eating well again. " });

        var created = await OkAsync(response, HttpStatusCode.Created);
        Assert.Equal(["id", "patientId", "consultationId", "text", "author", "createdAt"], created.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Owner called: eating well again.", created.GetProperty("text").GetString());
        var id = created.GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/clinical-notes/{id}", response.Headers.Location!.ToString());

        var read = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/clinical-notes/{id}");
        Assert.Equal(created.GetRawText(), read.GetRawText());
        var entries = await factory.AuditEntriesAsync(id);
        Assert.Equal([AuditActions.ClinicalNoteAdded, AuditActions.ClinicalNoteViewed], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.DoesNotContain("eating", e.Metadata));
    }

    [Fact]
    public async Task List_IsNewestFirst_AndAudited()
    {
        var clinic = await ClinicAsync();
        await OkAsync(await AddAsync(clinic.Nurse, clinic.PatientId, new { text = "First" }), HttpStatusCode.Created);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await OkAsync(await AddAsync(clinic.Vet, clinic.PatientId, new { text = "Second" }), HttpStatusCode.Created);

        var list = await clinic.Admin.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/clinical-notes");

        Assert.Equal(["Second", "First"], list.GetProperty("data").EnumerateArray().Select(n => n.GetProperty("text").GetString()));
        Assert.Contains(AuditActions.ClinicalNotesListed, (await factory.AuditEntriesAsync(clinic.PatientId)).Select(e => e.Action));
    }

    [Fact]
    public async Task InvalidNotes_AndBadConsultationLinks_Are422()
    {
        var clinic = await ClinicAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddAsync(clinic.Nurse, clinic.PatientId, new { text = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddAsync(clinic.Nurse, clinic.PatientId, new { text = new string('x', 4001) })).StatusCode);

        var discarded = await OpenConsultationAsync(clinic);
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{discarded}/discard", new { reason = "Mistake" }));
        var client = await clinic.Desk.PostAsJsonAsync("/api/patients", new { clientId = clinic.ClientId, name = "Tom", species = "Cat" });
        var otherPatient = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var otherPatientsConsultation = await OpenConsultationAsync(clinic, otherPatient);

        foreach (var consultationId in new[] { discarded, otherPatientsConsultation, Guid.NewGuid() })
        {
            var response = await AddAsync(clinic.Nurse, clinic.PatientId, new { text = "x", consultationId });
            Assert.Equal("consultationId", Assert.Single((await ErrorOf(response)).Details).Field);
        }

        var completed = await OpenConsultationAsync(clinic);
        await OkAsync(await clinic.Vet.PostAsync($"/api/consultations/{completed}/complete", null));
        var linked = await OkAsync(await AddAsync(clinic.Vet, clinic.PatientId, new { text = "Lab result received", consultationId = completed }), HttpStatusCode.Created);
        Assert.Equal(completed, linked.GetProperty("consultationId").GetGuid());
    }

    [Fact]
    public async Task Permissions_AndOtherClinics()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var theirs = (await OkAsync(await AddAsync(other.Nurse, other.PatientId, new { text = "x" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var inventory = await factory.SignInAsync(await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Inventory));

        foreach (var http in new[] { clinic.Admin, clinic.Desk, inventory })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await AddAsync(http, clinic.PatientId, new { text = "x" })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.GetAsync($"/api/patients/{clinic.PatientId}/clinical-notes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AddAsync(clinic.Nurse, other.PatientId, new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/clinical-notes/{theirs}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/patients/{other.PatientId}/clinical-notes")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await clinic.Vet.DeleteAsync($"/api/clinical-notes/{theirs}")).StatusCode);

        // Records of inactive animals still grow.
        await OkAsync(await clinic.Desk.PatchAsJsonAsync($"/api/patients/{clinic.PatientId}", new { status = "Inactive" }));
        Assert.Equal(HttpStatusCode.Created, (await AddAsync(clinic.Nurse, clinic.PatientId, new { text = "Moved away" })).StatusCode);
    }

    [Fact]
    public async Task History_ShowsNotes_ToClinicalReadersOnly()
    {
        var clinic = await ClinicAsync();
        var id = (await OkAsync(await AddAsync(clinic.Nurse, clinic.PatientId, new { text = "Phone advice given" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var forVet = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");
        var forDesk = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");

        var entry = forVet.GetProperty("data").EnumerateArray().Single(e => e.GetProperty("type").GetString() == "ClinicalNoteAdded");
        Assert.Equal(id, entry.GetProperty("id").GetGuid());
        Assert.Equal(["id", "text", "consultationId"], entry.GetProperty("details").GetProperty("note").EnumerateObject().Select(p => p.Name));
        Assert.Equal("Phone advice given", entry.GetProperty("details").GetProperty("note").GetProperty("text").GetString());
        Assert.DoesNotContain(forDesk.GetProperty("data").EnumerateArray(), e => e.GetProperty("type").GetString() == "ClinicalNoteAdded");
        Assert.Contains(id.ToString(), (await factory.AuditEntriesAsync(clinic.PatientId)).Last(e => e.Action == AuditActions.PatientHistoryViewed).Metadata);
    }

    [Theory]
    [InlineData("UPDATE clinical_notes SET text = 'rewritten' WHERE patient_id = {0}")]
    [InlineData("DELETE FROM clinical_notes WHERE patient_id = {0}")]
    [InlineData("TRUNCATE clinical_notes")]
    public async Task Database_KeepsNotesAppendOnly(string sql)
    {
        var clinic = await ClinicAsync();
        await OkAsync(await AddAsync(clinic.Nurse, clinic.PatientId, new { text = "x" }), HttpStatusCode.Created);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, clinic.PatientId)));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Database_RefusesNotesOnDiscardedConsultations()
    {
        var clinic = await ClinicAsync();
        var consultation = await OpenConsultationAsync(clinic);
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{consultation}/discard", new { reason = "Mistake" }));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO clinical_notes (id, clinic_id, patient_id, consultation_id, author_user_id, text, created_at) " +
            "SELECT gen_random_uuid(), clinic_id, patient_id, id, veterinarian_id, 'late', now() FROM consultations WHERE id = {0}", consultation)));

        Assert.Equal("42501", exception.SqlState);
    }
}
