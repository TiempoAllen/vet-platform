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

/// <summary>Appointment booking, editing, rescheduling and conflict validation (B-09) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class AppointmentsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// A clinic (UTC) with an administrator, a receptionist, a veterinarian working 08:00–20:00 every day, and a
    /// client with one active patient.
    /// </summary>
    private sealed record Clinic(HttpClient Admin, User AdminUser, HttpClient Desk, User DeskUser, User Vet, Guid ClientId, Guid PatientId);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var deskUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist);
        var desk = await factory.SignInAsync(deskUser);
        var vet = await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian);
        await AddWorkingWeekAsync(admin, vet.Id);
        var (clientId, patientId) = await AddPatientAsync(desk, "Jane", "Rex");
        return new Clinic(admin, adminUser, desk, deskUser, vet, clientId, patientId);
    }

    private static async Task AddWorkingWeekAsync(HttpClient admin, Guid vetId)
    {
        foreach (var day in Enum.GetNames<DayOfWeek>())
        {
            var response = await admin.PostAsJsonAsync(
                $"/api/veterinarians/{vetId}/weekly-hours",
                new { dayOfWeek = day, startTime = "08:00", endTime = "20:00", effectiveFrom = "2026-01-01" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    private static async Task<(Guid ClientId, Guid PatientId)> AddPatientAsync(HttpClient desk, string owner, string name)
    {
        var client = await desk.PostAsJsonAsync("/api/clients", new { firstName = owner, lastName = "Doe" });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var patient = await desk.PostAsJsonAsync("/api/patients", new { clientId, name, species = "Dog" });
        return (clientId, (await patient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    /// <summary>A UTC instant <paramref name="days"/> after today (the test clock), at hh:mm.</summary>
    private string At(int days, int hour, int minute = 0)
    {
        var today = factory.Clock.GetUtcNow().UtcDateTime.Date;
        return new DateTimeOffset(today.AddDays(days).AddHours(hour).AddMinutes(minute), TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:ssK");
    }

    private object Booking(Clinic clinic, int hour, int minutes = 30, Guid? patientId = null, Guid? vetId = null, int days = 7) => new
    {
        patientId = patientId ?? clinic.PatientId,
        veterinarianId = vetId ?? clinic.Vet.Id,
        startAt = At(days, hour),
        endAt = At(days, hour, minutes),
        type = "Consultation",
    };

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<string?>> ConflictFieldsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        return (await ErrorOf(response)).Details.Select(d => d.Field).ToList();
    }

    // ---------- Create ----------

    [Fact]
    public async Task Create_Returns201_WithSummariesAndTheClinicsOffset()
    {
        var clinic = await ClinicAsync();
        await clinic.Admin.PatchAsJsonAsync("/api/clinic", new { timeZone = "Asia/Tokyo" }); // UTC+9, no daylight saving
        var tokyoMorning = At(7, 1); // 10:00 in Tokyo, within 08:00–20:00 local hours

        var response = await clinic.Desk.PostAsJsonAsync("/api/appointments", new
        {
            patientId = clinic.PatientId, veterinarianId = clinic.Vet.Id, startAt = tokyoMorning, endAt = At(7, 1, 30),
            type = "Vaccination", reason = " Annual booster ", notes = "Bring records.",
        });

        var body = await CreatedAsync(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/appointments/{id}", response.Headers.Location!.ToString());
        Assert.EndsWith("+09:00", body.GetProperty("startAt").GetString());
        Assert.Equal(DateTimeOffset.Parse(tokyoMorning), body.GetProperty("startAt").GetDateTimeOffset());
        Assert.Equal("Rex", body.GetProperty("patient").GetProperty("name").GetString());
        Assert.Equal(clinic.ClientId, body.GetProperty("client").GetProperty("id").GetGuid());
        Assert.Equal(clinic.Vet.Id, body.GetProperty("veterinarian").GetProperty("id").GetGuid());
        Assert.Equal(clinic.DeskUser.Id, body.GetProperty("createdBy").GetProperty("id").GetGuid());
        Assert.Equal("Annual booster", body.GetProperty("reason").GetString());
        Assert.Equal("Scheduled", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("override").ValueKind);
        Assert.Equal(
            [
                "id", "patient", "client", "veterinarian", "startAt", "endAt", "type", "reason", "notes", "status",
                "override", "createdBy", "createdAt", "updatedAt",
            ],
            body.EnumerateObject().Select(p => p.Name));

        var fetched = await clinic.Admin.GetFromJsonAsync<JsonElement>($"/api/appointments/{id}");
        Assert.Equal(body.ToString(), fetched.ToString());
    }

    [Theory]
    [InlineData(Role.Veterinarian)]
    [InlineData(Role.Nurse)]
    [InlineData(Role.Receptionist)]
    [InlineData(Role.Admin)]
    public async Task ClinicStaff_CanBookAndEdit(Role role)
    {
        var clinic = await ClinicAsync();
        var http = await factory.SignInAsync(await factory.CreateColleagueAsync(clinic.AdminUser, roles: role));

        var id = (await CreatedAsync(await http.PostAsJsonAsync("/api/appointments", Booking(clinic, 9)))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await http.PatchAsJsonAsync($"/api/appointments/{id}", new { notes = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/appointments")).StatusCode);
    }

    [Theory]
    [InlineData(Role.Inventory)]
    [InlineData(null)]
    public async Task UsersWithoutAppointmentAccess_AreForbidden(Role? role)
    {
        var clinic = await ClinicAsync();
        var user = role is { } r
            ? await factory.CreateColleagueAsync(clinic.AdminUser, roles: r)
            : await factory.CreateColleagueAsync(clinic.AdminUser);
        var http = await factory.SignInAsync(user);
        var id = Guid.NewGuid();

        var responses = new[]
        {
            await http.GetAsync("/api/appointments"),
            await http.GetAsync($"/api/appointments/{id}"),
            await http.PostAsJsonAsync("/api/appointments", new { }),
            await http.PatchAsJsonAsync($"/api/appointments/{id}", new { notes = "x" }),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    [Theory]
    [InlineData("""{ "veterinarianId": "VET", "startAt": "S", "endAt": "E", "type": "Consultation" }""", "patientId")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "startAt": "S", "endAt": "E" }""", "type")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "startAt": "S+2", "endAt": "E", "type": "Consultation" }""", "startAt")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "startAt": "S", "endAt": "S", "type": "Consultation" }""", "endAt")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "startAt": "S", "endAt": "LONG", "type": "Consultation" }""", "endAt")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "startAt": "YESTERDAY", "endAt": "E", "type": "Consultation" }""", "startAt")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "startAt": "S", "endAt": "E", "type": "Consultation", "allowConflicts": true }""", "overrideReason")]
    [InlineData("""{ "patientId": "PAT", "veterinarianId": "VET", "startAt": "S", "endAt": "E", "type": "Consultation", "overrideReason": "x" }""", "overrideReason")]
    public async Task InvalidBookings_Are422(string json, string field)
    {
        var clinic = await ClinicAsync();
        var body = json
            .Replace("PAT", clinic.PatientId.ToString()).Replace("VET", clinic.Vet.Id.ToString())
            .Replace("\"S+2\"", $"\"{At(7, 9, 2)}\"").Replace("\"LONG\"", $"\"{At(7, 21, 5)}\"")
            .Replace("\"YESTERDAY\"", $"\"{At(-1, 9)}\"")
            .Replace("\"S\"", $"\"{At(7, 9)}\"").Replace("\"E\"", $"\"{At(7, 9, 30)}\"");

        var response = await clinic.Desk.PostAsync("/api/appointments", Json(body));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, d => d.Field == field);
    }

    [Theory]
    [InlineData("type", "\"checkup\"")]
    [InlineData("startAt", "\"2026-11-02T09:00:00\"")]
    public async Task BadlyTypedValues_Are400(string field, string value)
    {
        var clinic = await ClinicAsync();
        var json = $$"""
            { "patientId": "{{clinic.PatientId}}", "veterinarianId": "{{clinic.Vet.Id}}", "startAt": "{{At(7, 9)}}",
              "endAt": "{{At(7, 9, 30)}}", "type": "Consultation", "{{field}}": {{value}} }
            """;

        var response = await clinic.Desk.PostAsync("/api/appointments", Json(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EarlierToday_CanStillBeRecorded()
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Desk.PostAsJsonAsync("/api/appointments", new
        {
            patientId = clinic.PatientId, veterinarianId = clinic.Vet.Id, startAt = At(0, 0), endAt = At(0, 0, 30),
            type = "Emergency", allowConflicts = true, overrideReason = "Walk-in before opening",
        });

        await CreatedAsync(response);
    }

    [Fact]
    public async Task OnlyActivePatients_AndActiveVetsOfTheClinic_CanBeBooked()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var (_, deceased) = await AddPatientAsync(clinic.Desk, "Old", "Gone");
        await clinic.Desk.PatchAsJsonAsync($"/api/patients/{deceased}", new { status = "Deceased" });
        var disabledVet = await factory.CreateColleagueAsync(clinic.AdminUser, UserStatus.Disabled, Role.Veterinarian);
        var nurse = await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Nurse);

        foreach (var (patient, field) in new[] { (deceased, "patientId"), (other.PatientId, "patientId"), (Guid.NewGuid(), "patientId") })
        {
            var response = await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9, patientId: patient));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(field, Assert.Single((await ErrorOf(response)).Details).Field);
        }

        foreach (var vet in new[] { disabledVet.Id, nurse.Id, other.Vet.Id })
        {
            var response = await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9, vetId: vet));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var detail = Assert.Single((await ErrorOf(response)).Details);
            Assert.Equal("veterinarianId", detail.Field);
            Assert.Equal("The veterinarian was not found or is not active.", detail.Message);
        }
    }

    // ---------- Conflicts and overrides ----------

    [Fact]
    public async Task OutsideWorkingHours_ExceptionsAndClosures_Conflict()
    {
        var clinic = await ClinicAsync();
        await clinic.Admin.PostAsJsonAsync("/api/schedule-exceptions", new
        {
            veterinarianId = clinic.Vet.Id, type = "Training", startsAt = At(8, 10), endsAt = At(8, 12),
        });
        await clinic.Admin.PostAsJsonAsync("/api/schedule-exceptions", new { type = "Closure", startsAt = At(9, 0), endsAt = At(10, 0) });

        Assert.Equal(["startAt"], await ConflictFieldsAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 19, 90))));
        Assert.Equal(["startAt"], await ConflictFieldsAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 11, days: 8))));
        Assert.Equal(["startAt"], await ConflictFieldsAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9, days: 9))));
        await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 12, days: 8))); // after training
    }

    [Fact]
    public async Task DoubleBookings_ConflictForTheVetAndThePatient_ButBackToBackIsFine()
    {
        var clinic = await ClinicAsync();
        var (_, otherPatient) = await AddPatientAsync(clinic.Desk, "John", "Tom");
        var otherVet = await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Veterinarian);
        await AddWorkingWeekAsync(clinic.Admin, otherVet.Id);
        await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9)));

        var vetClash = await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9, patientId: otherPatient));
        var patientClash = await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9, vetId: otherVet.Id));
        var both = await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9));

        Assert.Equal(["veterinarianId"], await ConflictFieldsAsync(vetClash));
        Assert.Equal(["patientId"], await ConflictFieldsAsync(patientClash));
        Assert.Equal(["veterinarianId", "patientId"], await ConflictFieldsAsync(both));
        await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", new
        {
            patientId = otherPatient, veterinarianId = clinic.Vet.Id, startAt = At(7, 9, 30), endAt = At(7, 10), type = "FollowUp",
        }));
    }

    [Fact]
    public async Task Override_AcceptsEveryConflict_AndRecordsWhichOnes()
    {
        var clinic = await ClinicAsync();
        await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 19, 60)));

        var response = await clinic.Desk.PostAsJsonAsync("/api/appointments", new
        {
            patientId = clinic.PatientId, veterinarianId = clinic.Vet.Id, startAt = At(7, 19, 30), endAt = At(7, 20, 30),
            type = "Emergency", allowConflicts = true, overrideReason = "  Emergency surgery ",
        });

        var body = await CreatedAsync(response);
        var @override = body.GetProperty("override");
        Assert.Equal("Emergency surgery", @override.GetProperty("reason").GetString());
        Assert.Equal(
            ["OutsideWorkingHours", "VeterinarianDoubleBooked", "PatientDoubleBooked"],
            @override.GetProperty("conflicts").EnumerateArray().Select(c => c.GetString()));

        var entry = Assert.Single(await factory.AuditEntriesAsync(body.GetProperty("id").GetGuid()));
        Assert.Equal(AuditActions.AppointmentCreated, entry.Action);
        Assert.Contains("VeterinarianDoubleBooked", entry.Metadata);
    }

    [Fact]
    public async Task Override_WithoutConflicts_IsNotRecorded()
    {
        var clinic = await ClinicAsync();

        var body = await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", new
        {
            patientId = clinic.PatientId, veterinarianId = clinic.Vet.Id, startAt = At(7, 9), endAt = At(7, 9, 30),
            type = "Consultation", allowConflicts = true, overrideReason = "Just in case",
        }));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("override").ValueKind);
    }

    [Fact]
    public async Task ConcurrentBookingsOfTheSameSlot_OnlyOneWins()
    {
        var clinic = await ClinicAsync();
        var patients = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            patients.Add((await AddPatientAsync(clinic.Desk, $"Owner{i}", $"Pet{i}")).PatientId);
        }

        var responses = await Task.WhenAll(patients.Select(patient =>
            clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 10, patientId: patient))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    // ---------- Update and reschedule ----------

    [Fact]
    public async Task Reschedule_RechecksConflicts_AndAMoveToAFreeSlotClearsTheOverride()
    {
        var clinic = await ClinicAsync();
        await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9)));
        var (_, otherPatient) = await AddPatientAsync(clinic.Desk, "John", "Tom");
        var id = (await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 11, patientId: otherPatient))))
            .GetProperty("id").GetGuid();

        var clash = await clinic.Desk.PatchAsJsonAsync($"/api/appointments/{id}", new { startAt = At(7, 9), endAt = At(7, 9, 30) });
        Assert.Equal(["veterinarianId"], await ConflictFieldsAsync(clash));

        var forced = await clinic.Desk.PatchAsJsonAsync($"/api/appointments/{id}", new
        {
            startAt = At(7, 9), endAt = At(7, 9, 30), allowConflicts = true, overrideReason = "Owner insists",
        });
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);

        var notesOnly = await clinic.Desk.PatchAsJsonAsync($"/api/appointments/{id}", new { notes = "Owner called" });
        Assert.Equal("Owner insists", (await notesOnly.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("override").GetProperty("reason").GetString());

        var moved = await clinic.Desk.PatchAsJsonAsync($"/api/appointments/{id}", new { startAt = At(7, 14), endAt = At(7, 14, 45) });
        var body = await moved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("override").ValueKind);
        Assert.Equal("Owner called", body.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task ChangingThePatient_MovesTheClientWithIt()
    {
        var clinic = await ClinicAsync();
        var (otherClient, otherPatient) = await AddPatientAsync(clinic.Desk, "John", "Tom");
        var id = (await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9)))).GetProperty("id").GetGuid();

        var body = await (await clinic.Desk.PatchAsJsonAsync($"/api/appointments/{id}", new { patientId = otherPatient }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(otherPatient, body.GetProperty("patient").GetProperty("id").GetGuid());
        Assert.Equal(otherClient, body.GetProperty("client").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Update_ChangesOnlyGivenFields_AndBlankClearsText()
    {
        var clinic = await ClinicAsync();
        var created = await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", new
        {
            patientId = clinic.PatientId, veterinarianId = clinic.Vet.Id, startAt = At(7, 9), endAt = At(7, 9, 30),
            type = "Consultation", reason = "Limping", notes = "Nervous dog",
        }));
        var id = created.GetProperty("id").GetGuid();

        var body = await (await clinic.Desk.PatchAsJsonAsync($"/api/appointments/{id}", new { type = "FollowUp", notes = "" }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("FollowUp", body.GetProperty("type").GetString());
        Assert.Equal("Limping", body.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("notes").ValueKind);
        Assert.Equal(created.GetProperty("startAt").GetString(), body.GetProperty("startAt").GetString());
        Assert.Equal(
            [AuditActions.AppointmentCreated, AuditActions.AppointmentUpdated],
            (await factory.AuditEntriesAsync(id)).Select(e => e.Action));
    }

    [Fact]
    public async Task OnlyScheduledAppointments_CanBeEdited()
    {
        var clinic = await ClinicAsync();
        var id = (await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9)))).GetProperty("id").GetGuid();
        await factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE appointments SET status = 'Completed' WHERE id = {0}", id));

        var response = await clinic.Desk.PatchAsJsonAsync($"/api/appointments/{id}", new { notes = "late edit" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Reschedule_IntoThePast_Is422()
    {
        var clinic = await ClinicAsync();
        var id = (await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9)))).GetProperty("id").GetGuid();

        var response = await clinic.Desk.PatchAsJsonAsync($"/api/appointments/{id}", new { startAt = At(-2, 9), endAt = At(-2, 9, 30) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("startAt", Assert.Single((await ErrorOf(response)).Details).Field);
    }

    // ---------- List and tenant isolation ----------

    [Fact]
    public async Task List_FiltersAndSortsByStart()
    {
        var clinic = await ClinicAsync();
        var (otherClient, otherPatient) = await AddPatientAsync(clinic.Desk, "John", "Tom");
        async Task<Guid> Book(int days, int hour, Guid patient) =>
            (await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, hour, patientId: patient, days: days))))
                .GetProperty("id").GetGuid();
        var late = await Book(8, 15, clinic.PatientId);
        var early = await Book(7, 9, otherPatient);
        var later = await Book(20, 9, clinic.PatientId);

        async Task<List<Guid>> Ids(string query) =>
            (await clinic.Desk.GetFromJsonAsync<JsonElement>("/api/appointments?" + query)).GetProperty("data")
                .EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([early, late, later], await Ids(""));
        Assert.Equal([early, late], await Ids($"from={Uri.EscapeDataString(At(7, 0))}&to={Uri.EscapeDataString(At(9, 0))}"));
        Assert.Equal([late, later], await Ids($"patientId={clinic.PatientId}"));
        Assert.Equal([early], await Ids($"clientId={otherClient}"));
        Assert.Equal([early, late, later], await Ids($"veterinarianId={clinic.Vet.Id}&status=Scheduled"));
        Assert.Empty(await Ids("status=Cancelled"));
    }

    [Theory]
    [InlineData("from=2026-11-01", "from")]
    [InlineData("status=scheduled", "status")]
    [InlineData("from=2026-11-02T00:00:00Z&to=2026-11-01T00:00:00Z", "to")]
    public async Task List_RejectsInvalidParameters(string query, string field)
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Desk.GetAsync("/api/appointments?" + query);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, d => d.Field == field);
    }

    [Fact]
    public async Task OtherClinicsAppointments_AreInvisible()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var theirs = (await CreatedAsync(await other.Desk.PostAsJsonAsync("/api/appointments", Booking(other, 9)))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.GetAsync($"/api/appointments/{theirs}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.PatchAsJsonAsync($"/api/appointments/{theirs}", new { notes = "x" })).StatusCode);
        Assert.Empty((await clinic.Desk.GetFromJsonAsync<JsonElement>("/api/appointments")).GetProperty("data").EnumerateArray());

        // Same slot in another clinic is no conflict.
        await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9)));
    }

    [Fact]
    public async Task Appointments_CannotBeDeleted()
    {
        var clinic = await ClinicAsync();
        var id = (await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9)))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await clinic.Desk.DeleteAsync($"/api/appointments/{id}")).StatusCode);
    }

    // ---------- Database rules ----------

    [Fact]
    public async Task Database_ExcludesOverlappingUnoverriddenBookingsOfOneVet()
    {
        var clinic = await ClinicAsync();
        var (_, otherPatient) = await AddPatientAsync(clinic.Desk, "John", "Tom");
        var first = (await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9)))).GetProperty("id").GetGuid();
        var second = (await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 10, patientId: otherPatient)))).GetProperty("id").GetGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE appointments SET start_at = (SELECT start_at FROM appointments WHERE id = {0}), end_at = (SELECT end_at FROM appointments WHERE id = {0}) WHERE id = {1}",
                first, second)));

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, exception.SqlState);
    }

    [Theory]
    [InlineData("UPDATE appointments SET end_at = start_at + interval '2 minutes' WHERE id = {0}", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE appointments SET end_at = start_at + interval '13 hours' WHERE id = {0}", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE appointments SET override_reason = 'x' WHERE id = {0}", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE appointments SET status = 'Lost' WHERE id = {0}", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE appointments SET start_at = start_at + interval '1 minute' WHERE id = {0}", PostgresErrorCodes.CheckViolation)]
    public async Task Database_RejectsInvalidAppointments(string sql, string sqlState)
    {
        var clinic = await ClinicAsync();
        var id = (await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9)))).GetProperty("id").GetGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal(sqlState, exception.SqlState);
    }

    [Fact]
    public async Task Database_RefusesAnotherClinicsPatientOrVet()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var id = (await CreatedAsync(await clinic.Desk.PostAsJsonAsync("/api/appointments", Booking(clinic, 9)))).GetProperty("id").GetGuid();

        foreach (var (sql, value) in new[]
                 {
                     ("UPDATE appointments SET patient_id = {0} WHERE id = {1}", other.PatientId),
                     ("UPDATE appointments SET veterinarian_id = {0} WHERE id = {1}", other.Vet.Id),
                     ("UPDATE appointments SET client_id = {0} WHERE id = {1}", other.ClientId),
                 })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
                db.Database.ExecuteSqlRawAsync(sql, value, id)));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        }
    }
}
