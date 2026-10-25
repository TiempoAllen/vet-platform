using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Vaccinations, the patient's list and the clinic's due list (B-14) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class VaccinationsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed record Clinic(
        HttpClient Admin, User AdminUser, HttpClient Vet, User VetUser, HttpClient Nurse, HttpClient Desk, Guid ClientId, Guid PatientId);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var vetUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian);
        var nurse = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Nurse));
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        var clientId = await ClientAsync(desk, "Jane", "Doe");
        var patientId = await PatientAsync(desk, clientId, "Rex");
        return new Clinic(admin, adminUser, await factory.SignInAsync(vetUser), vetUser, nurse, desk, clientId, patientId);
    }

    private static async Task<Guid> ClientAsync(HttpClient desk, string first, string last)
    {
        var client = await desk.PostAsJsonAsync("/api/clients", new { firstName = first, lastName = last, phone = "555 0100", email = "owner@example.test" });
        return (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PatientAsync(HttpClient desk, Guid clientId, string name)
    {
        var patient = await desk.PostAsJsonAsync("/api/patients", new { clientId, name, species = "Dog" });
        return (await patient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private DateOnly Today => DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd");

    private async Task<JsonElement> RecordAsync(Clinic clinic, object body, Guid? patientId = null, HttpClient? http = null) =>
        await OkAsync(
            await (http ?? clinic.Nurse).PostAsJsonAsync($"/api/patients/{patientId ?? clinic.PatientId}/vaccinations", body),
            HttpStatusCode.Created);

    private object Dose(Clinic clinic, string name = "DHPPi", int daysAgo = 0, int? dueInDays = 365) => new
    {
        vaccineName = name, administeredOn = Day(Today.AddDays(-daysAgo)),
        nextDueOn = dueInDays is { } d ? Day(Today.AddDays(-daysAgo + d)) : null,
        veterinarianId = clinic.VetUser.Id, manufacturer = "VetPharma", lotNumber = "L-123",
    };

    private Task<JsonElement> DueAsync(HttpClient http, string query) =>
        http.GetFromJsonAsync<JsonElement>($"/api/vaccinations/due?{query}")!;

    // ---------- Recording ----------

    [Fact]
    public async Task ANurse_RecordsADoseGivenHere()
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Nurse.PostAsJsonAsync($"/api/patients/{clinic.PatientId}/vaccinations", new
        {
            vaccineName = " Rabies ", administeredOn = Day(Today), nextDueOn = Day(Today.AddYears(3)),
            veterinarianId = clinic.VetUser.Id, manufacturer = "VetPharma", lotNumber = "R-77", lotExpiresOn = Day(Today.AddYears(1)),
            notes = "Left hind leg",
        });

        var created = await OkAsync(response, HttpStatusCode.Created);
        Assert.Equal(
            ["id", "patientId", "consultationId", "vaccineName", "manufacturer", "lotNumber", "lotExpiresOn", "administeredOn",
             "nextDueOn", "administeredElsewhere", "veterinarian", "notes", "status", "voidedAt", "voidedBy", "voidReason",
             "recordedBy", "createdAt", "updatedAt"],
            created.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Rabies", created.GetProperty("vaccineName").GetString());
        Assert.Equal(Day(Today), created.GetProperty("administeredOn").GetString());
        Assert.Equal("Active", created.GetProperty("status").GetString());
        Assert.Equal(clinic.VetUser.Id, created.GetProperty("veterinarian").GetProperty("id").GetGuid());
        var id = created.GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/vaccinations/{id}", response.Headers.Location!.ToString());

        var read = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/vaccinations/{id}");
        Assert.Equal(created.GetRawText(), read.GetRawText());
        var entries = await factory.AuditEntriesAsync(id);
        Assert.Equal([AuditActions.VaccinationRecorded, AuditActions.VaccinationViewed], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.DoesNotContain("Rabies", e.Metadata));
    }

    [Fact]
    public async Task DosesGivenElsewhere_NameThePlace_AndNoVeterinarian()
    {
        var clinic = await ClinicAsync();

        var elsewhere = await RecordAsync(clinic, new { vaccineName = "Leptospirosis", administeredOn = "2024-03-01", administeredElsewhere = " Previous clinic " });
        Assert.Equal("Previous clinic", elsewhere.GetProperty("administeredElsewhere").GetString());
        Assert.Equal(JsonValueKind.Null, elsewhere.GetProperty("veterinarian").ValueKind);

        var nurseUser = await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Nurse);
        var cases = new (object Body, string Field)[]
        {
            (new { vaccineName = "X", administeredOn = "2024-03-01", administeredElsewhere = "Elsewhere", veterinarianId = clinic.VetUser.Id }, "veterinarianId"),
            (new { vaccineName = "X", administeredOn = Day(Today) }, "veterinarianId"),
            (new { vaccineName = "X", administeredOn = Day(Today), veterinarianId = nurseUser.Id }, "veterinarianId"),
            (new { vaccineName = "X", administeredOn = Day(Today), administeredElsewhere = "  " }, "veterinarianId"),
        };
        foreach (var (body, field) in cases)
        {
            var response = await clinic.Nurse.PostAsJsonAsync($"/api/patients/{clinic.PatientId}/vaccinations", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(field, Assert.Single((await ErrorOf(response)).Details).Field);
        }
    }

    [Theory]
    [InlineData("""{ "administeredOn": "2025-01-01", "administeredElsewhere": "x" }""", HttpStatusCode.UnprocessableEntity, "VaccineName")]
    [InlineData("""{ "vaccineName": "X", "administeredElsewhere": "x" }""", HttpStatusCode.UnprocessableEntity, "AdministeredOn")]
    [InlineData("""{ "vaccineName": "X", "administeredOn": "1999-12-31", "administeredElsewhere": "x" }""", HttpStatusCode.UnprocessableEntity, "administeredOn")]
    [InlineData("""{ "vaccineName": "X", "administeredOn": "2025-01-01", "nextDueOn": "2025-01-01", "administeredElsewhere": "x" }""", HttpStatusCode.UnprocessableEntity, "nextDueOn")]
    [InlineData("""{ "vaccineName": "X", "administeredOn": "2025-01-01", "nextDueOn": "2101-01-01", "administeredElsewhere": "x" }""", HttpStatusCode.UnprocessableEntity, "nextDueOn")]
    [InlineData("""{ "vaccineName": "X", "administeredOn": "2025-01-01", "lotExpiresOn": "1990-01-01", "administeredElsewhere": "x" }""", HttpStatusCode.UnprocessableEntity, "lotExpiresOn")]
    [InlineData("""{ "vaccineName": "X", "administeredOn": "01/02/2025", "administeredElsewhere": "x" }""", HttpStatusCode.BadRequest, null)]
    [InlineData("""{ "vaccineName": "X", "administeredOn": "2025-01-01", "administeredElsewhere": "x", "consultationId": "00000000-0000-0000-0000-000000000001" }""", HttpStatusCode.UnprocessableEntity, "consultationId")]
    public async Task InvalidDoses_AreRejected(string json, HttpStatusCode expected, string? field)
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Nurse.PostAsync($"/api/patients/{clinic.PatientId}/vaccinations", Json(json));

        Assert.Equal(expected, response.StatusCode);
        if (field is not null)
        {
            Assert.Contains((await ErrorOf(response)).Details, d => string.Equals(d.Field, field, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task AFutureDose_Is422()
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Nurse.PostAsJsonAsync($"/api/patients/{clinic.PatientId}/vaccinations", new
        {
            vaccineName = "X", administeredOn = Day(Today.AddDays(1)), veterinarianId = clinic.VetUser.Id,
        });

        Assert.Equal("administeredOn", Assert.Single((await ErrorOf(response)).Details).Field);
    }

    [Fact]
    public async Task ConsultationLinks_MustBeANonDiscardedConsultationOfThePatient()
    {
        var clinic = await ClinicAsync();
        var open = (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var linked = await RecordAsync(clinic, new { vaccineName = "DHPPi", administeredOn = Day(Today), veterinarianId = clinic.VetUser.Id, consultationId = open });
        Assert.Equal(open, linked.GetProperty("consultationId").GetGuid());

        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{open}/discard", new { reason = "Wrong patient" }));
        var otherPatient = await PatientAsync(clinic.Desk, clinic.ClientId, "Tom");
        var otherOpen = (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = otherPatient, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var other = await ClinicAsync();
        var foreign = (await OkAsync(await other.Vet.PostAsJsonAsync("/api/consultations", new { patientId = other.PatientId, veterinarianId = other.VetUser.Id }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        foreach (var consultationId in new[] { open, otherOpen, foreign })
        {
            var response = await clinic.Nurse.PostAsJsonAsync($"/api/patients/{clinic.PatientId}/vaccinations", new
            {
                vaccineName = "DHPPi", administeredOn = Day(Today), veterinarianId = clinic.VetUser.Id, consultationId,
            });
            Assert.Equal("consultationId", Assert.Single((await ErrorOf(response)).Details).Field);
        }

        // A completed consultation can still be linked (vaccines recorded after sign-off).
        var done = (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Vet.PostAsync($"/api/consultations/{done}/complete", null));
        await RecordAsync(clinic, new { vaccineName = "Rabies", administeredOn = Day(Today), veterinarianId = clinic.VetUser.Id, consultationId = done });
    }

    [Fact]
    public async Task InactivePatients_Are409_AndOtherClinicsPatientsAre404()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        await OkAsync(await clinic.Desk.PatchAsJsonAsync($"/api/patients/{clinic.PatientId}", new { status = "Inactive" }));

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Nurse.PostAsJsonAsync($"/api/patients/{clinic.PatientId}/vaccinations", Dose(clinic))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Nurse.PostAsJsonAsync($"/api/patients/{other.PatientId}/vaccinations", Dose(clinic))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Nurse.PostAsJsonAsync($"/api/patients/{Guid.NewGuid()}/vaccinations", Dose(clinic))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/patients/{other.PatientId}/vaccinations")).StatusCode);
        var theirs = (await RecordAsync(other, Dose(other))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/vaccinations/{theirs}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.PostAsJsonAsync($"/api/vaccinations/{theirs}/void", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task Permissions_ClinicalRolesRecord_ReceptionOnlySeesTheDueList()
    {
        var clinic = await ClinicAsync();
        var id = (await RecordAsync(clinic, Dose(clinic), http: clinic.Vet)).GetProperty("id").GetGuid();
        var inventory = await factory.SignInAsync(await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Inventory));

        foreach (var http in new[] { clinic.Admin, clinic.Desk, inventory })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync($"/api/patients/{clinic.PatientId}/vaccinations", Dose(clinic))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync($"/api/vaccinations/{id}/void", new { reason = "x" })).StatusCode);
        }

        foreach (var http in new[] { clinic.Desk, inventory })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/api/patients/{clinic.PatientId}/vaccinations")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/api/vaccinations/{id}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await clinic.Admin.GetAsync($"/api/patients/{clinic.PatientId}/vaccinations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clinic.Desk.GetAsync($"/api/vaccinations/due?to={Day(Today.AddYears(2))}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inventory.GetAsync($"/api/vaccinations/due?to={Day(Today)}")).StatusCode);
        var me = await clinic.Desk.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Contains(Permissions.VaccinationsReadDue, me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    // ---------- Voiding ----------

    [Fact]
    public async Task Voiding_KeepsTheRecord_WithWhoWhenAndWhy()
    {
        var clinic = await ClinicAsync();
        var id = (await RecordAsync(clinic, Dose(clinic))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Nurse.PostAsJsonAsync($"/api/vaccinations/{id}/void", new { reason = " " })).StatusCode);

        var voided = await OkAsync(await clinic.Nurse.PostAsJsonAsync($"/api/vaccinations/{id}/void", new { reason = " Wrong patient chart " }));

        Assert.Equal("Voided", voided.GetProperty("status").GetString());
        Assert.Equal("Wrong patient chart", voided.GetProperty("voidReason").GetString());
        Assert.NotEqual(JsonValueKind.Null, voided.GetProperty("voidedAt").ValueKind);
        Assert.Equal("DHPPi", voided.GetProperty("vaccineName").GetString());
        var read = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/vaccinations/{id}");
        Assert.Equal(voided.GetRawText(), read.GetRawText());
        var entry = (await factory.AuditEntriesAsync(id)).Single(e => e.Action == AuditActions.VaccinationVoided);
        Assert.DoesNotContain("Wrong patient", entry.Metadata);

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Nurse.PostAsJsonAsync($"/api/vaccinations/{id}/void", new { reason = "again" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Nurse.PostAsJsonAsync($"/api/vaccinations/{Guid.NewGuid()}/void", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/vaccinations/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await clinic.Vet.DeleteAsync($"/api/vaccinations/{id}")).StatusCode);
    }

    // ---------- Lists ----------

    [Fact]
    public async Task PatientList_IsNewestFirst_FiltersByStatus_AndIsAudited()
    {
        var clinic = await ClinicAsync();
        await RecordAsync(clinic, Dose(clinic, "Old", daysAgo: 400));
        var newest = (await RecordAsync(clinic, Dose(clinic, "New"))).GetProperty("id").GetGuid();
        var voided = (await RecordAsync(clinic, Dose(clinic, "Mistake", daysAgo: 10))).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Nurse.PostAsJsonAsync($"/api/vaccinations/{voided}/void", new { reason = "x" }));

        var all = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/vaccinations");
        var active = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/vaccinations?status=Active");

        Assert.Equal(["New", "Mistake", "Old"], all.GetProperty("data").EnumerateArray().Select(v => v.GetProperty("vaccineName").GetString()));
        Assert.Equal(2, active.GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.Contains(newest.ToString(), (await factory.AuditEntriesAsync(clinic.PatientId)).Last(e => e.Action == AuditActions.VaccinationsListed).Metadata);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Vet.GetAsync($"/api/patients/{clinic.PatientId}/vaccinations?status=voided")).StatusCode);
    }

    [Fact]
    public async Task DueList_ShowsTheLatestActiveDosePerVaccine_WithTheOwnersContact()
    {
        var clinic = await ClinicAsync();
        // Rex: DHPPi given 400 days ago (due 35 days ago, overdue), then again today: only the newest counts.
        await RecordAsync(clinic, Dose(clinic, "DHPPi", daysAgo: 400));
        await RecordAsync(clinic, Dose(clinic, "dhppi", dueInDays: 30));
        // Rex: rabies overdue; a newer dose was voided, so the earlier one is the latest active dose.
        await RecordAsync(clinic, Dose(clinic, "Rabies", daysAgo: 100, dueInDays: 90));
        var voided = (await RecordAsync(clinic, Dose(clinic, "Rabies", daysAgo: 5, dueInDays: 1000))).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Nurse.PostAsJsonAsync($"/api/vaccinations/{voided}/void", new { reason = "x" }));
        // Rex: kennel cough series ended by a newer dose without a due date.
        await RecordAsync(clinic, Dose(clinic, "Kennel cough", daysAgo: 300, dueInDays: 310));
        await RecordAsync(clinic, Dose(clinic, "Kennel cough", daysAgo: 2, dueInDays: null));
        // Tom (inactive) is never due.
        var tom = await PatientAsync(clinic.Desk, clinic.ClientId, "Tom");
        await RecordAsync(clinic, Dose(clinic, "Rabies", daysAgo: 400), patientId: tom);
        await OkAsync(await clinic.Desk.PatchAsJsonAsync($"/api/patients/{tom}", new { status = "Inactive" }));
        // Another clinic's due doses never appear.
        var other = await ClinicAsync();
        await RecordAsync(other, Dose(other, "Rabies", daysAgo: 400));

        var due = await DueAsync(clinic.Desk, $"to={Day(Today.AddDays(60))}");

        var items = due.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["Rabies", "dhppi"], items.Select(i => i.GetProperty("vaccineName").GetString()));
        Assert.Equal(Day(Today.AddDays(-10)), items[0].GetProperty("nextDueOn").GetString());
        Assert.Equal(Day(Today.AddDays(-100)), items[0].GetProperty("lastAdministeredOn").GetString());
        Assert.Equal(
            ["vaccinationId", "vaccineName", "lastAdministeredOn", "nextDueOn", "patient", "client"],
            items[0].EnumerateObject().Select(p => p.Name));
        var client = items[0].GetProperty("client");
        Assert.Equal("555 0100", client.GetProperty("phone").GetString());
        Assert.Equal(clinic.ClientId, client.GetProperty("id").GetGuid());
        Assert.Equal("Rex", items[0].GetProperty("patient").GetProperty("name").GetString());

        var window = await DueAsync(clinic.Desk, $"from={Day(Today)}&to={Day(Today.AddDays(60))}");
        Assert.Equal(["dhppi"], window.GetProperty("data").EnumerateArray().Select(i => i.GetProperty("vaccineName").GetString()));
        var audited = (await factory.AuditEntriesAsync(clinic.AdminUser.ClinicId)).Last(e => e.Action == AuditActions.VaccinationsDueListed);
        Assert.DoesNotContain("Rabies", audited.Metadata);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Desk.GetAsync("/api/vaccinations/due")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Desk.GetAsync($"/api/vaccinations/due?from={Day(Today)}&to={Day(Today.AddDays(-1))}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Desk.GetAsync("/api/vaccinations/due?to=2026-13-01")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Desk.GetAsync($"/api/vaccinations/due?from=1999-12-31&to={Day(Today)}")).StatusCode);
    }

    // ---------- History ----------

    [Fact]
    public async Task History_ShowsDosesToClinicalReadersOnly_OnTheLocalAdministeredDay()
    {
        var clinic = await ClinicAsync();
        await clinic.Admin.PatchAsJsonAsync("/api/clinic", new { timeZone = "Asia/Tokyo" }); // UTC+9, no daylight saving
        var id = (await RecordAsync(clinic, new { vaccineName = "Rabies", administeredOn = "2024-03-01", administeredElsewhere = "Previous clinic", nextDueOn = "2027-03-01" })).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Nurse.PostAsJsonAsync($"/api/vaccinations/{id}/void", new { reason = "Duplicate" }));

        var forVet = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");
        var forDesk = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");

        var entry = forVet.GetProperty("data").EnumerateArray().Single(e => e.GetProperty("type").GetString() == "VaccinationGiven");
        Assert.Equal("2024-02-29T15:00:00+00:00", entry.GetProperty("occurredAt").GetString()); // 2024-03-01 00:00 in Tokyo
        var vaccination = entry.GetProperty("details").GetProperty("vaccination");
        Assert.Equal(
            ["id", "vaccineName", "administeredOn", "nextDueOn", "administeredElsewhere", "veterinarian", "status"],
            vaccination.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Voided", vaccination.GetProperty("status").GetString());
        Assert.DoesNotContain(forDesk.GetProperty("data").EnumerateArray(), e => e.GetProperty("type").GetString() == "VaccinationGiven");
        Assert.Contains(id.ToString(), (await factory.AuditEntriesAsync(clinic.PatientId)).Last(e => e.Action == AuditActions.PatientHistoryViewed).Metadata);
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE vaccinations SET vaccine_name = 'rewritten' WHERE id = {0}")]
    [InlineData("UPDATE vaccinations SET next_due_on = next_due_on + 1 WHERE id = {0}")]
    [InlineData("DELETE FROM vaccinations WHERE id = {0}")]
    [InlineData("UPDATE vaccinations SET status = 'Voided', voided_at = now(), voided_by_user_id = recorded_by_user_id, void_reason = 'x', lot_number = 'other' WHERE id = {0}")]
    [InlineData("TRUNCATE vaccinations")]
    public async Task Database_NeverChangesARecord_ExceptToVoidIt(string sql)
    {
        var clinic = await ClinicAsync();
        var id = (await RecordAsync(clinic, Dose(clinic))).GetProperty("id").GetGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));
        Assert.Equal("42501", exception.SqlState);

        await factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE vaccinations SET status = 'Voided', voided_at = now(), voided_by_user_id = recorded_by_user_id, void_reason = 'x' WHERE id = {0}", id));
        var undo = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE vaccinations SET status = 'Active', voided_at = NULL, voided_by_user_id = NULL, void_reason = NULL WHERE id = {0}", id)));
        Assert.Equal("42501", undo.SqlState);
    }

    [Fact]
    public async Task Database_ALinkedInsert_WaitsForAConcurrentDiscard_AndIsThenRefused()
    {
        var clinic = await ClinicAsync();
        var consultation = (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Transaction A discards the consultation and holds its row lock without committing.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VetPlatform.Api.Common.Persistence.AppDbContext>();
        await using var discard = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE consultations SET status = 'Discarded', discarded_at = started_at, discard_reason = 'x' WHERE id = {0}", consultation);

        // Transaction B links a dose to it: the trigger must wait for A instead of reading the old status.
        var insert = factory.UpdateDatabaseAsync(other => other.Database.ExecuteSqlRawAsync(
            "INSERT INTO vaccinations (id, clinic_id, patient_id, consultation_id, vaccine_name, administered_on, administered_day_starts_at, " +
            "administered_elsewhere, status, created_at, updated_at) " +
            "SELECT gen_random_uuid(), clinic_id, patient_id, id, 'X', DATE '2025-01-01', now(), 'Elsewhere', 'Active', now(), now() " +
            "FROM consultations WHERE id = {0}", consultation));
        Assert.NotSame(insert, await Task.WhenAny(insert, Task.Delay(TimeSpan.FromMilliseconds(500))));

        await discard.CommitAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => insert);
        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Database_RefusesVoidedInserts_DiscardedLinks_AndInconsistentGivers()
    {
        var clinic = await ClinicAsync();
        var consultation = (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{consultation}/discard", new { reason = "Mistake" }));
        // Columns after the fixed ones: consultation_id, administered_elsewhere, veterinarian_id, status and the void fields.
        const string prefix =
            "INSERT INTO vaccinations (id, clinic_id, patient_id, vaccine_name, administered_on, administered_day_starts_at, created_at, updated_at, " +
            "consultation_id, administered_elsewhere, veterinarian_id, status, voided_at, voided_by_user_id, void_reason) " +
            "SELECT gen_random_uuid(), c.clinic_id, c.patient_id, 'X', DATE '2025-01-01', now(), now(), now(), ";
        const string suffix = " FROM consultations c WHERE c.id = {0}";

        const string voidedInsert = prefix + "NULL, 'Elsewhere', NULL, 'Voided', now(), c.veterinarian_id, 'x'" + suffix;
        const string discardedLink = prefix + "c.id, 'Elsewhere', NULL, 'Active', NULL, NULL, NULL" + suffix;
        const string nobodyGaveIt = prefix + "NULL, NULL, NULL, 'Active', NULL, NULL, NULL" + suffix;
        const string bothGivers = prefix + "NULL, 'Elsewhere', c.veterinarian_id, 'Active', NULL, NULL, NULL" + suffix;

        async Task<string> Fails(string sql)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, consultation)));
            return exception.SqlState;
        }

        Assert.Equal("42501", await Fails(voidedInsert));
        Assert.Equal("42501", await Fails(discardedLink));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await Fails(nobodyGaveIt));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await Fails(bothGivers));
    }
}
