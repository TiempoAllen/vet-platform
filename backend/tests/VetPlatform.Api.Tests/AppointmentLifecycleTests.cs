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

/// <summary>Appointment status lifecycle and its patient-history entries (B-10) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class AppointmentLifecycleTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    /// <summary>A UTC clinic with an admin, a receptionist, a vet working 08:00–20:00 daily, and one patient.</summary>
    private sealed record Clinic(HttpClient Admin, User AdminUser, HttpClient Desk, User DeskUser, User Vet, Guid PatientId);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var deskUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist);
        var desk = await factory.SignInAsync(deskUser);
        var vet = await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian);
        foreach (var day in Enum.GetNames<DayOfWeek>())
        {
            await admin.PostAsJsonAsync(
                $"/api/veterinarians/{vet.Id}/weekly-hours",
                new { dayOfWeek = day, startTime = "08:00", endTime = "20:00", effectiveFrom = "2026-01-01" });
        }

        return new Clinic(admin, adminUser, desk, deskUser, vet, await AddPatientAsync(desk, "Rex"));
    }

    private static async Task<Guid> AddPatientAsync(HttpClient desk, string name)
    {
        var client = await desk.PostAsJsonAsync("/api/clients", new { firstName = "Jane", lastName = "Doe" });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var patient = await desk.PostAsJsonAsync("/api/patients", new { clientId, name, species = "Dog" });
        return (await patient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private string At(int days, int hour, int minute = 0)
    {
        var today = factory.Clock.GetUtcNow().UtcDateTime.Date;
        return new DateTimeOffset(today.AddDays(days).AddHours(hour).AddMinutes(minute), TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:ssK");
    }

    /// <summary>Books today 00:00–00:30 (already started; overrides "outside working hours"), or another day at 09:00.</summary>
    private async Task<Guid> BookAsync(Clinic clinic, int days = 0, Guid? patientId = null)
    {
        var (startAt, endAt) = days == 0 ? (At(0, 0), At(0, 0, 30)) : (At(days, 9), At(days, 9, 30));
        var response = await clinic.Desk.PostAsJsonAsync("/api/appointments", new
        {
            patientId = patientId ?? clinic.PatientId, veterinarianId = clinic.Vet.Id, startAt, endAt, type = "Consultation",
            allowConflicts = days == 0 ? true : (bool?)null, overrideReason = days == 0 ? "Early walk-in" : null,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ActAsync(HttpClient http, Guid id, string action, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await http.PostAsJsonAsync($"/api/appointments/{id}/{action}", body ?? new { });
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Status(JsonElement appointment) => appointment.GetProperty("status").GetString()!;

    // ---------- Happy paths ----------

    [Fact]
    public async Task CheckIn_ThenComplete_RecordsTimes_AndIsFinal()
    {
        var clinic = await ClinicAsync();
        var id = await BookAsync(clinic);

        var checkedIn = await ActAsync(clinic.Desk, id, "check-in");
        Assert.Equal("CheckedIn", Status(checkedIn));
        Assert.Equal(factory.Clock.GetUtcNow().ToUnixTimeSeconds(), checkedIn.GetProperty("checkedInAt").GetDateTimeOffset().ToUnixTimeSeconds());

        var completed = await ActAsync(clinic.Desk, id, "complete");
        Assert.Equal("Completed", Status(completed));
        Assert.Equal(JsonValueKind.String, completed.GetProperty("checkedInAt").ValueKind);
        Assert.Equal(JsonValueKind.String, completed.GetProperty("completedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("cancellation").ValueKind);

        foreach (var action in new[] { "check-in", "undo-check-in", "complete", "no-show", "undo-no-show" })
        {
            Assert.Equal(ErrorCodes.Conflict, (await ErrorOf(await clinic.Desk.PostAsJsonAsync($"/api/appointments/{id}/{action}", new { }))).Code);
        }

        await ActAsync(clinic.Desk, id, "cancel", new { cancelledBy = "Client" }, HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Desk.PatchAsJsonAsync($"/api/appointments/{id}", new { notes = "x" })).StatusCode);

        Assert.Equal(
            [AuditActions.AppointmentCreated, AuditActions.AppointmentCheckedIn, AuditActions.AppointmentCompleted],
            (await factory.AuditEntriesAsync(id)).Select(e => e.Action));
    }

    [Fact]
    public async Task Cancel_RecordsWhoAndWhy_FromScheduledOrCheckedIn_AndFreesTheSlot()
    {
        var clinic = await ClinicAsync();
        var scheduled = await BookAsync(clinic, days: 7);
        var checkedInId = await BookAsync(clinic, patientId: await AddPatientAsync(clinic.Desk, "Tom"));
        await ActAsync(clinic.Desk, checkedInId, "check-in");

        var cancelled = await ActAsync(clinic.Desk, scheduled, "cancel", new { cancelledBy = "Client", reason = " Owner unwell " });
        var cancellation = cancelled.GetProperty("cancellation");
        Assert.Equal("Cancelled", Status(cancelled));
        Assert.Equal("Client", cancellation.GetProperty("by").GetString());
        Assert.Equal("Owner unwell", cancellation.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.String, cancellation.GetProperty("at").ValueKind);

        var afterCheckIn = await ActAsync(clinic.Desk, checkedInId, "cancel", new { cancelledBy = "Clinic" });
        Assert.Equal(JsonValueKind.String, afterCheckIn.GetProperty("checkedInAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, afterCheckIn.GetProperty("cancellation").GetProperty("reason").ValueKind);

        // The cancelled slot can be booked again without a conflict.
        var rebook = await clinic.Desk.PostAsJsonAsync("/api/appointments", new
        {
            patientId = clinic.PatientId, veterinarianId = clinic.Vet.Id, startAt = At(7, 9), endAt = At(7, 9, 30), type = "Consultation",
        });
        Assert.Equal(HttpStatusCode.Created, rebook.StatusCode);

        var entry = (await factory.AuditEntriesAsync(scheduled)).Last();
        Assert.Equal(AuditActions.AppointmentCancelled, entry.Action);
        Assert.Contains("Client", entry.Metadata);
        Assert.DoesNotContain("unwell", entry.Metadata);
    }

    [Theory]
    [InlineData("""{ }""", HttpStatusCode.UnprocessableEntity)]
    [InlineData("""{ "cancelledBy": "Owner" }""", HttpStatusCode.BadRequest)]
    [InlineData("""{ "cancelledBy": "client" }""", HttpStatusCode.BadRequest)]
    public async Task Cancel_NeedsAnExactCancelledBy(string json, HttpStatusCode expected)
    {
        var clinic = await ClinicAsync();
        var id = await BookAsync(clinic, days: 7);

        var response = await clinic.Desk.PostAsync(
            $"/api/appointments/{id}/cancel", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(expected, response.StatusCode);
    }

    // ---------- Timing rules ----------

    [Fact]
    public async Task CheckIn_OnlyOnTheAppointmentsDay_AndCompleteOnlyAfterCheckIn()
    {
        var clinic = await ClinicAsync();
        var nextWeek = await BookAsync(clinic, days: 7);
        var today = await BookAsync(clinic, patientId: await AddPatientAsync(clinic.Desk, "Tom"));

        var early = await clinic.Desk.PostAsJsonAsync($"/api/appointments/{nextWeek}/check-in", new { });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("own day", (await ErrorOf(early)).Message);

        await ActAsync(clinic.Desk, today, "complete", expected: HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task NoShow_OnlyAfterTheStart_AndUndoRestoresIt()
    {
        var clinic = await ClinicAsync();
        var future = await BookAsync(clinic, days: 7);
        var started = await BookAsync(clinic, patientId: await AddPatientAsync(clinic.Desk, "Tom"));

        await ActAsync(clinic.Desk, future, "no-show", expected: HttpStatusCode.Conflict);

        var noShow = await ActAsync(clinic.Desk, started, "no-show");
        Assert.Equal("NoShow", Status(noShow));
        Assert.Equal(JsonValueKind.String, noShow.GetProperty("noShowAt").ValueKind);

        var restored = await ActAsync(clinic.Desk, started, "undo-no-show");
        Assert.Equal("Scheduled", Status(restored));
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("noShowAt").ValueKind);
    }

    [Fact]
    public async Task UndoNoShow_IsRefused_WhenTheSlotWasBookedAgain()
    {
        var clinic = await ClinicAsync();
        var original = await BookAsync(clinic);
        await ActAsync(clinic.Desk, original, "no-show");
        await BookAsync(clinic, patientId: await AddPatientAsync(clinic.Desk, "Tom")); // same vet, same slot

        var response = await clinic.Desk.PostAsJsonAsync($"/api/appointments/{original}/undo-no-show", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["veterinarianId"], (await ErrorOf(response)).Details.Select(d => d.Field));
    }

    [Fact]
    public async Task UndoNoShow_IsRefused_EvenIfTheBookingHadOverriddenADoubleBooking()
    {
        var clinic = await ClinicAsync();
        var existing = await BookAsync(clinic, patientId: await AddPatientAsync(clinic.Desk, "First"));
        var overridden = await BookAsync(clinic); // overrides VeterinarianDoubleBooked against `existing`
        await ActAsync(clinic.Desk, existing, "cancel", new { cancelledBy = "Clinic" });
        await ActAsync(clinic.Desk, overridden, "no-show");
        await BookAsync(clinic, patientId: await AddPatientAsync(clinic.Desk, "Newcomer")); // booked into the freed slot

        var response = await clinic.Desk.PostAsJsonAsync($"/api/appointments/{overridden}/undo-no-show", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["veterinarianId"], (await ErrorOf(response)).Details.Select(d => d.Field));
    }

    [Fact]
    public async Task LifecycleTimes_CarryTheClinicsOffset_AndHistoryTimesAreUtc()
    {
        var clinic = await ClinicAsync();
        await clinic.Admin.PatchAsJsonAsync("/api/clinic", new { timeZone = "Asia/Tokyo" }); // UTC+9 all year
        var id = await BookAsync(clinic, days: 7);

        var cancelled = await ActAsync(clinic.Desk, id, "cancel", new { cancelledBy = "Client" });

        Assert.EndsWith("+09:00", cancelled.GetProperty("cancellation").GetProperty("at").GetString());
        var entry = (await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history"))
            .GetProperty("data")[0];
        Assert.EndsWith("+00:00", entry.GetProperty("occurredAt").GetString());
        Assert.EndsWith("+00:00", entry.GetProperty("details").GetProperty("appointment").GetProperty("startAt").GetString());
    }

    [Fact]
    public async Task AnotherClinicsPatientHistory_WithFinishedAppointments_IsNotFound()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var theirs = await BookAsync(other);
        await ActAsync(other.Desk, theirs, "check-in");
        await ActAsync(other.Desk, theirs, "complete");

        var response = await clinic.Desk.GetAsync($"/api/patients/{other.PatientId}/history");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UndoCheckIn_SameDayOnly()
    {
        var clinic = await ClinicAsync();
        var undone = await BookAsync(clinic);
        var later = await BookAsync(clinic, patientId: await AddPatientAsync(clinic.Desk, "Tom"));
        await ActAsync(clinic.Desk, undone, "check-in");
        await ActAsync(clinic.Desk, later, "check-in");

        var restored = await ActAsync(clinic.Desk, undone, "undo-check-in");
        Assert.Equal("Scheduled", Status(restored));
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("checkedInAt").ValueKind);

        factory.Clock.Advance(TimeSpan.FromDays(1));
        var desk = await factory.SignInAsync(clinic.DeskUser); // the old session ended with the day
        var tooLate = await desk.PostAsJsonAsync($"/api/appointments/{later}/undo-check-in", new { });

        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
        Assert.Contains("on the day it was made", (await ErrorOf(tooLate)).Message);
    }

    // ---------- Permissions, tenancy, concurrency ----------

    [Theory]
    [InlineData(Role.Veterinarian)]
    [InlineData(Role.Nurse)]
    public async Task ClinicalStaff_CanRunTheLifecycle(Role role)
    {
        var clinic = await ClinicAsync();
        var http = await factory.SignInAsync(await factory.CreateColleagueAsync(clinic.AdminUser, roles: role));
        var id = await BookAsync(clinic);

        await ActAsync(http, id, "check-in");
        await ActAsync(http, id, "complete");
    }

    [Fact]
    public async Task UsersWithoutAppointmentsManage_AreForbidden_AndOtherClinicsAppointmentsAreNotFound()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var inventory = await factory.SignInAsync(await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Inventory));
        var id = await BookAsync(clinic);
        var theirs = await BookAsync(other);

        foreach (var action in new[] { "check-in", "undo-check-in", "complete", "cancel", "no-show", "undo-no-show" })
        {
            var forbidden = await inventory.PostAsJsonAsync($"/api/appointments/{id}/{action}", new { cancelledBy = "Clinic" });
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            var notFound = await clinic.Desk.PostAsJsonAsync($"/api/appointments/{theirs}/{action}", new { cancelledBy = "Clinic" });
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }

        var untouched = await other.Desk.GetFromJsonAsync<JsonElement>($"/api/appointments/{theirs}");
        Assert.Equal("Scheduled", Status(untouched));
    }

    [Fact]
    public async Task ConcurrentTransitions_AreSerialised()
    {
        var clinic = await ClinicAsync();
        var id = await BookAsync(clinic);
        await ActAsync(clinic.Desk, id, "check-in");

        var results = await Task.WhenAll(
            clinic.Desk.PostAsJsonAsync($"/api/appointments/{id}/complete", new { }),
            clinic.Admin.PostAsJsonAsync($"/api/appointments/{id}/cancel", new { cancelledBy = "Clinic" }));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    // ---------- Patient history ----------

    [Fact]
    public async Task FinishedAppointments_AppearInThePatientHistory_NewestFirst_WithoutTheReason()
    {
        var clinic = await ClinicAsync();
        var completed = await BookAsync(clinic);
        await ActAsync(clinic.Desk, completed, "check-in");
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await ActAsync(clinic.Desk, completed, "complete");
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var cancelled = await BookAsync(clinic, days: 7);
        await ActAsync(clinic.Desk, cancelled, "cancel", new { cancelledBy = "Client", reason = "Private reason" });
        await BookAsync(clinic, days: 8); // scheduled: not history

        var history = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");
        var data = history.GetProperty("data").EnumerateArray().ToList();

        Assert.Equal(
            ["AppointmentCancelled", "AppointmentCompleted", "PatientRegistered"],
            data.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal(3, history.GetProperty("pagination").GetProperty("total").GetInt32());

        var cancelledEntry = data[0];
        Assert.Equal(cancelled, cancelledEntry.GetProperty("id").GetGuid());
        Assert.Equal(clinic.DeskUser.Id, cancelledEntry.GetProperty("actor").GetProperty("id").GetGuid());
        Assert.Equal(["appointment", "cancelledBy"], cancelledEntry.GetProperty("details").EnumerateObject().Select(p => p.Name));
        Assert.Equal("Client", cancelledEntry.GetProperty("details").GetProperty("cancelledBy").GetString());
        Assert.DoesNotContain("Private reason", history.ToString());

        var appointment = data[1].GetProperty("details").GetProperty("appointment");
        Assert.Equal(["id", "startAt", "endAt", "type", "veterinarian"], appointment.EnumerateObject().Select(p => p.Name));
        Assert.Equal(clinic.Vet.Id, appointment.GetProperty("veterinarian").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task NoShows_AppearInTheHistory_AndDisappearWhenUndone()
    {
        var clinic = await ClinicAsync();
        var id = await BookAsync(clinic);
        await ActAsync(clinic.Desk, id, "no-show");

        async Task<List<string?>> Types() =>
            (await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history"))
                .GetProperty("data").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();

        Assert.Equal(["AppointmentNoShow", "PatientRegistered"], await Types());
        await ActAsync(clinic.Desk, id, "undo-no-show");
        Assert.Equal(["PatientRegistered"], await Types());
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE appointments SET status = 'Completed' WHERE id = {0}")]
    [InlineData("UPDATE appointments SET checked_in_at = now() WHERE id = {0}")]
    [InlineData("UPDATE appointments SET cancelled_by = 'Client' WHERE id = {0}")]
    [InlineData("UPDATE appointments SET cancellation_reason = 'x' WHERE id = {0}")]
    [InlineData("UPDATE appointments SET status = 'Cancelled', cancelled_at = now() WHERE id = {0}")]
    [InlineData("UPDATE appointments SET status = 'Cancelled', cancelled_at = now(), cancelled_by = 'Bogus' WHERE id = {0}")]
    [InlineData("UPDATE appointments SET status = 'Completed', checked_in_at = now(), completed_at = now() - interval '1 hour' WHERE id = {0}")]
    public async Task Database_KeepsStatusAndTimestampsConsistent(string sql)
    {
        var clinic = await ClinicAsync();
        var id = await BookAsync(clinic, days: 7);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }
}
