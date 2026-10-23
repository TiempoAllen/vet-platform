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

/// <summary>Consultations: SOAP notes, vitals, completion, addenda, read auditing and history (B-11).</summary>
[Trait("Category", "Database")]
public sealed class ConsultationsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed record Clinic(
        HttpClient Admin, User AdminUser, HttpClient Vet, User VetUser, HttpClient Nurse, HttpClient Desk, Guid PatientId);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var vetUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian);
        var nurse = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Nurse));
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        return new Clinic(admin, adminUser, await factory.SignInAsync(vetUser), vetUser, nurse, desk, await AddPatientAsync(desk, "Rex"));
    }

    private static async Task<Guid> AddPatientAsync(HttpClient desk, string name)
    {
        var client = await desk.PostAsJsonAsync("/api/clients", new { firstName = "Jane", lastName = "Doe" });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var patient = await desk.PostAsJsonAsync("/api/patients", new { clientId, name, species = "Dog" });
        return (await patient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> OpenAsync(HttpClient http, Clinic clinic, object? extra = null, Guid? patientId = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["patientId"] = patientId ?? clinic.PatientId,
            ["veterinarianId"] = clinic.VetUser.Id,
        };
        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                body[property.Name] = property.GetValue(extra);
            }
        }

        return (await OkAsync(await http.PostAsJsonAsync("/api/consultations", body), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> Act(HttpClient http, Guid id, string action, object? body = null) =>
        http.PostAsJsonAsync($"/api/consultations/{id}/{action}", body ?? new { });

    private string Today(int hour, int minute = 0) =>
        new DateTimeOffset(factory.Clock.GetUtcNow().UtcDateTime.Date.AddHours(hour).AddMinutes(minute), TimeSpan.Zero)
            .ToString("yyyy-MM-dd'T'HH:mm:ssK");

    private async Task<Guid> CheckedInAppointmentAsync(Clinic clinic, Guid patientId, string status = "check-in")
    {
        var booked = await OkAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", new
        {
            patientId, veterinarianId = clinic.VetUser.Id, startAt = Today(0), endAt = Today(0, 30), type = "Consultation",
            allowConflicts = true, overrideReason = "Test",
        }), HttpStatusCode.Created);
        var id = booked.GetProperty("id").GetGuid();
        if (status == "check-in")
        {
            await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/appointments/{id}/check-in", new { }));
        }

        return id;
    }

    // ---------- Open, edit, complete ----------

    [Fact]
    public async Task Vet_OpensEditsAndCompletes_AndTheWeightUpdatesThePatient()
    {
        var clinic = await ClinicAsync();

        var created = await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new
        {
            patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id, subjective = " Limping for two days ",
            weightKg = 31.25m, temperatureC = 38.6m, heartRate = 96, respiratoryRate = 24, bodyConditionScore = 5, painScore = 3,
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal("InProgress", created.GetProperty("status").GetString());
        Assert.Equal("Limping for two days", created.GetProperty("subjective").GetString());
        Assert.Equal(31.25m, created.GetProperty("vitals").GetProperty("weightKg").GetDecimal());
        Assert.Equal(38.6m, created.GetProperty("vitals").GetProperty("temperatureC").GetDecimal());
        Assert.Equal(
            [
                "id", "patient", "appointmentId", "veterinarian", "status", "startedAt", "completedAt", "completedBy",
                "discardedAt", "discardReason", "subjective", "objective", "assessment", "plan", "vitals", "diagnoses",
                "treatments", "addenda",
                "createdBy", "createdAt", "updatedAt",
            ],
            created.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["weightKg", "temperatureC", "heartRate", "respiratoryRate", "bodyConditionScore", "painScore"],
            created.GetProperty("vitals").EnumerateObject().Select(p => p.Name));

        var edited = await OkAsync(await clinic.Vet.PatchAsync($"/api/consultations/{id}", Json(
            """{ "assessment": "Soft tissue injury", "plan": "Rest, review in 7 days", "subjective": "", "painScore": null }""")));
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("subjective").ValueKind);
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("vitals").GetProperty("painScore").ValueKind);
        Assert.Equal(96, edited.GetProperty("vitals").GetProperty("heartRate").GetInt32());

        var completed = await OkAsync(await Act(clinic.Vet, id, "complete"));
        Assert.Equal("Completed", completed.GetProperty("status").GetString());
        Assert.Equal(clinic.VetUser.Id, completed.GetProperty("completedBy").GetProperty("id").GetGuid());

        var patient = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}");
        Assert.Equal(31.25m, patient.GetProperty("weightKg").GetDecimal());
        Assert.Contains(
            AuditActions.PatientUpdated, (await factory.AuditEntriesAsync(clinic.PatientId)).Select(e => e.Action));
    }

    [Fact]
    public async Task Nurses_OpenAndEdit_ButOnlyVeterinariansComplete()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic.Nurse, clinic, new { objective = "Alert and responsive" });

        Assert.Equal(HttpStatusCode.OK, (await clinic.Nurse.PatchAsJsonAsync($"/api/consultations/{id}", new { plan = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(clinic.Nurse, id, "complete")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Act(clinic.Vet, id, "complete")).StatusCode);
    }

    [Fact]
    public async Task Admins_ReadButNeverWrite_AndReceptionAndInventoryHaveNoAccess()
    {
        var clinic = await ClinicAsync();
        var inventory = await factory.SignInAsync(await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Inventory));
        var id = await OpenAsync(clinic.Vet, clinic);

        Assert.Equal(HttpStatusCode.OK, (await clinic.Admin.GetAsync($"/api/consultations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Admin.PatchAsJsonAsync($"/api/consultations/{id}", new { plan = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(clinic.Admin, id, "complete")).StatusCode);

        foreach (var http in new[] { clinic.Desk, inventory })
        {
            var responses = new[]
            {
                await http.GetAsync($"/api/consultations/{id}"),
                await http.GetAsync($"/api/patients/{clinic.PatientId}/consultations"),
                await http.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id }),
                await http.PatchAsJsonAsync($"/api/consultations/{id}", new { plan = "x" }),
                await Act(http, id, "complete"),
                await Act(http, id, "discard", new { reason = "x" }),
                await Act(http, id, "addenda", new { text = "x" }),
            };
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        }
    }

    [Theory]
    [InlineData("""{ "veterinarianId": "VET" }""", "patientId")]
    [InlineData("""{ "patientId": "PAT" }""", "veterinarianId")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "PAT" }""", "veterinarianId")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "weightKg": 0 }""", "weightKg")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "temperatureC": 37.25 }""", "temperatureC")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "temperatureC": 50 }""", "temperatureC")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "heartRate": 0 }""", "heartRate")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "respiratoryRate": 201 }""", "respiratoryRate")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "bodyConditionScore": 10 }""", "bodyConditionScore")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "painScore": -1 }""", "painScore")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "appointmentId": "PAT" }""", "appointmentId")]
    public async Task InvalidOpenings_Are422(string json, string field)
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Vet.PostAsync("/api/consultations", Json(
            json.Replace("PAT", clinic.PatientId.ToString()).Replace("VET", clinic.VetUser.Id.ToString())));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, d => d.Field == field);
    }

    [Fact]
    public async Task InactivePatients_AndOtherClinicsPatients_CannotBeSeen()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var inactive = await AddPatientAsync(clinic.Desk, "Old");
        await clinic.Desk.PatchAsJsonAsync($"/api/patients/{inactive}", new { status = "Inactive" });

        foreach (var patient in new[] { inactive, other.PatientId })
        {
            var response = await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = patient, veterinarianId = clinic.VetUser.Id });
            Assert.Equal("patientId", Assert.Single((await ErrorOf(response)).Details).Field);
        }
    }

    [Fact]
    public async Task AnotherClinicsVetOrAppointment_CannotBeReferenced()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var theirAppointment = await CheckedInAppointmentAsync(other, other.PatientId);

        var vet = await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = other.VetUser.Id });
        var appointment = await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id, appointmentId = theirAppointment });

        Assert.Equal("veterinarianId", Assert.Single((await ErrorOf(vet)).Details).Field);
        Assert.Equal("appointmentId", Assert.Single((await ErrorOf(appointment)).Details).Field);
    }

    [Fact]
    public async Task OneOpenConsultationPerPatient_EvenUnderConcurrency()
    {
        var clinic = await ClinicAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(2, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var open = (await results.Single(r => r.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        await OkAsync(await Act(clinic.Vet, open, "complete"));
        await OpenAsync(clinic.Vet, clinic); // the next one is fine once the first is completed
    }

    [Fact]
    public async Task AppointmentLink_NeedsACheckedInAppointmentOfThePatient_OncePerAppointment()
    {
        var clinic = await ClinicAsync();
        var otherPatient = await AddPatientAsync(clinic.Desk, "Tom");
        var scheduled = await CheckedInAppointmentAsync(clinic, otherPatient, status: "scheduled");
        var checkedIn = await CheckedInAppointmentAsync(clinic, clinic.PatientId);

        foreach (var appointment in new[] { scheduled, checkedIn })
        {
            var target = appointment == scheduled ? otherPatient : await AddPatientAsync(clinic.Desk, "Wrong");
            var response = await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = target, veterinarianId = clinic.VetUser.Id, appointmentId = appointment });
            Assert.Equal("appointmentId", Assert.Single((await ErrorOf(response)).Details).Field);
        }

        var first = await OpenAsync(clinic.Vet, clinic, new { appointmentId = checkedIn });
        await OkAsync(await Act(clinic.Vet, first, "discard", new { reason = "Opened on the wrong appointment" }));
        var second = await OpenAsync(clinic.Vet, clinic, new { appointmentId = checkedIn }); // discarded ones free it
        await OkAsync(await Act(clinic.Vet, second, "complete"));

        var third = await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id, appointmentId = checkedIn });
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
    }

    // ---------- Completed records are frozen; addenda ----------

    [Fact]
    public async Task CompletedConsultations_AreFrozen_AndCorrectedOnlyByAddenda()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic.Vet, clinic, new { assessment = "Otitis" });
        Assert.Equal(HttpStatusCode.Conflict, (await Act(clinic.Vet, id, "addenda", new { text = "Too early" })).StatusCode);
        await OkAsync(await Act(clinic.Vet, id, "complete"));

        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{id}", new { assessment = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(clinic.Vet, id, "complete")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(clinic.Vet, id, "discard", new { reason = "x" })).StatusCode);

        var withAddendum = await OkAsync(await Act(clinic.Nurse, id, "addenda", new { text = " Left ear, not right. " }));
        var addendum = Assert.Single(withAddendum.GetProperty("addenda").EnumerateArray());
        Assert.Equal("Left ear, not right.", addendum.GetProperty("text").GetString());
        Assert.Equal(["id", "text", "author", "createdAt"], addendum.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Otitis", withAddendum.GetProperty("assessment").GetString());

        var blank = await Act(clinic.Vet, id, "addenda", new { text = "   " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);

        // A second addendum: the first one is loaded and tracked, and must not be written again (its trigger would refuse).
        var two = await OkAsync(await Act(clinic.Vet, id, "addenda", new { text = "Recheck booked." }));
        Assert.Equal(["Left ear, not right.", "Recheck booked."], two.GetProperty("addenda").EnumerateArray().Select(a => a.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task Addenda_AreCappedPerConsultation()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic.Vet, clinic);
        await OkAsync(await Act(clinic.Vet, id, "complete"));
        await factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO consultation_addenda (id, clinic_id, consultation_id, author_user_id, text, created_at) " +
            "SELECT gen_random_uuid(), clinic_id, id, veterinarian_id, 'note ' || n, now() FROM consultations, generate_series(1, 50) n WHERE id = {0}",
            id));

        var response = await Act(clinic.Vet, id, "addenda", new { text = "One too many" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task WriteResponses_EqualALaterRead_EvenWithSubMicrosecondClocks()
    {
        var clinic = await ClinicAsync();
        factory.Clock.Advance(TimeSpan.FromTicks(3));

        var created = await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        factory.Clock.Advance(TimeSpan.FromTicks(7));
        var completed = await OkAsync(await Act(clinic.Vet, id, "complete"));
        factory.Clock.Advance(TimeSpan.FromTicks(5));
        var withAddendum = await OkAsync(await Act(clinic.Vet, id, "addenda", new { text = "Note" }));

        var read = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/consultations/{id}");
        Assert.Equal(created.GetProperty("startedAt").GetString(), read.GetProperty("startedAt").GetString());
        Assert.Equal(completed.GetProperty("completedAt").GetString(), read.GetProperty("completedAt").GetString());
        Assert.Equal(withAddendum.GetProperty("addenda")[0].GetProperty("createdAt").GetString(), read.GetProperty("addenda")[0].GetProperty("createdAt").GetString());
    }

    [Theory]
    [InlineData("UPDATE consultations SET assessment = 'rewritten' WHERE id = {0}")]
    [InlineData("UPDATE consultations SET status = 'InProgress', completed_at = NULL, completed_by_user_id = NULL WHERE id = {0}")]
    public async Task Database_RefusesToChangeACompletedConsultation(string sql)
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic.Vet, clinic, new { assessment = "Signed" });
        await OkAsync(await Act(clinic.Vet, id, "complete"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public async Task Database_RefusesAddendaOnUnsignedConsultations()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic.Vet, clinic);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO consultation_addenda (id, clinic_id, consultation_id, author_user_id, text, created_at) " +
            "SELECT gen_random_uuid(), clinic_id, id, veterinarian_id, 'sneaky', now() FROM consultations WHERE id = {0}", id)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Theory]
    [InlineData("UPDATE consultation_addenda SET text = 'rewritten' WHERE consultation_id = {0}")]
    [InlineData("DELETE FROM consultation_addenda WHERE consultation_id = {0}")]
    public async Task Database_RefusesToChangeOrDeleteAddenda(string sql)
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic.Vet, clinic);
        await OkAsync(await Act(clinic.Vet, id, "complete"));
        await OkAsync(await Act(clinic.Vet, id, "addenda", new { text = "Signed note" }));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public async Task Discard_NeedsAReason_AndHidesTheConsultationFromTheDefaultList()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic.Vet, clinic);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(clinic.Vet, id, "discard", new { })).StatusCode);
        var discarded = await OkAsync(await Act(clinic.Vet, id, "discard", new { reason = "Wrong patient" }));
        Assert.Equal("Discarded", discarded.GetProperty("status").GetString());
        Assert.Equal("Wrong patient", discarded.GetProperty("discardReason").GetString());

        var list = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/consultations");
        Assert.Equal(0, list.GetProperty("pagination").GetProperty("total").GetInt32());
        var onlyDiscarded = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/consultations?status=Discarded");
        Assert.Equal(id, onlyDiscarded.GetProperty("data")[0].GetProperty("id").GetGuid());
    }

    // ---------- Read auditing, history, tenancy ----------

    [Fact]
    public async Task ClinicalReads_AreAudited_WithoutNoteText()
    {
        var clinic = await ClinicAsync();
        var id = await OpenAsync(clinic.Vet, clinic, new { assessment = "Confidential finding" });
        await OkAsync(await Act(clinic.Vet, id, "complete"));

        await clinic.Admin.GetAsync($"/api/consultations/{id}");
        await clinic.Admin.GetAsync($"/api/patients/{clinic.PatientId}/consultations");
        await clinic.Admin.GetAsync($"/api/patients/{clinic.PatientId}/history");
        await clinic.Desk.GetAsync($"/api/patients/{clinic.PatientId}/history"); // no clinical entries shown: not a clinical read

        var consultationEntries = await factory.AuditEntriesAsync(id);
        Assert.Contains(consultationEntries, e => e.Action == AuditActions.ConsultationViewed && e.ActorUserId == clinic.AdminUser.Id);
        var patientEntries = await factory.AuditEntriesAsync(clinic.PatientId);
        Assert.Single(patientEntries, e => e.Action == AuditActions.ConsultationsListed);
        Assert.Single(patientEntries, e => e.Action == AuditActions.PatientHistoryViewed);
        Assert.All(consultationEntries.Concat(patientEntries), e => Assert.DoesNotContain("Confidential", e.Metadata ?? ""));
    }

    [Fact]
    public async Task CompletedConsultations_AppearInTheHistory_OnlyForClinicalReaders()
    {
        var clinic = await ClinicAsync();
        var inProgressPatient = await AddPatientAsync(clinic.Desk, "Tom");
        await OpenAsync(clinic.Vet, clinic, patientId: inProgressPatient);
        var id = await OpenAsync(clinic.Vet, clinic, new { assessment = "Otitis externa" });
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        await OkAsync(await Act(clinic.Vet, id, "complete"));

        var forVet = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");
        var forDesk = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");

        var entry = forVet.GetProperty("data")[0];
        Assert.Equal("ConsultationCompleted", entry.GetProperty("type").GetString());
        Assert.Equal(clinic.VetUser.Id, entry.GetProperty("actor").GetProperty("id").GetGuid());
        var consultation = entry.GetProperty("details").GetProperty("consultation");
        Assert.Equal(["id", "startedAt", "completedAt", "appointmentId", "veterinarian", "assessment", "diagnoses"], consultation.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Otitis externa", consultation.GetProperty("assessment").GetString());
        Assert.Equal(2, forVet.GetProperty("pagination").GetProperty("total").GetInt32());

        Assert.Equal(["PatientRegistered"], forDesk.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("type").GetString()));
        Assert.Equal(1, forDesk.GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.DoesNotContain("Otitis", forDesk.ToString());

        var otherHistory = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{inProgressPatient}/history");
        Assert.Equal(["PatientRegistered"], otherHistory.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task OtherClinicsConsultations_AreNotFound()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var theirs = await OpenAsync(other.Vet, other);

        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/consultations/{theirs}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.PatchAsJsonAsync($"/api/consultations/{theirs}", new { plan = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Act(clinic.Vet, theirs, "complete")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Act(clinic.Vet, theirs, "addenda", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Act(clinic.Vet, theirs, "discard", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/patients/{other.PatientId}/consultations")).StatusCode);

        var untouched = await other.Vet.GetFromJsonAsync<JsonElement>($"/api/consultations/{theirs}");
        Assert.Equal("InProgress", untouched.GetProperty("status").GetString());
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE consultations SET status = 'Completed' WHERE id = {0}", "23514")]
    [InlineData("UPDATE consultations SET status = 'Discarded', discarded_at = now() WHERE id = {0}", "23514")]
    [InlineData("UPDATE consultations SET status = 'Discarded', discarded_at = now(), discard_reason = '  ' WHERE id = {0}", "23514")]
    [InlineData("UPDATE consultations SET temperature_c = 60 WHERE id = {0}", "23514")]
    [InlineData("UPDATE consultations SET body_condition_score = 0 WHERE id = {0}", "23514")]
    [InlineData("UPDATE consultations SET veterinarian_id = (SELECT id FROM users WHERE clinic_id <> consultations.clinic_id LIMIT 1) WHERE id = {0}", "23503")]
    public async Task Database_KeepsConsultationsConsistent(string sql, string sqlState)
    {
        var clinic = await ClinicAsync();
        await ClinicAsync(); // another clinic's users exist
        var id = await OpenAsync(clinic.Vet, clinic);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal(sqlState, exception.SqlState);
    }
}
