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

/// <summary>Clinic time zone, veterinarians' weekly hours, exceptions, closures and computed hours (B-08).</summary>
[Trait("Category", "Database")]
public sealed class SchedulingTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed record Clinic(HttpClient Admin, User AdminUser, HttpClient Vet, User VetUser);

    /// <summary>A new clinic with an administrator and a veterinarian, both signed in.</summary>
    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var vetUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian);
        return new Clinic(admin, adminUser, await factory.SignInAsync(vetUser), vetUser);
    }

    private async Task<HttpClient> ColleagueAsync(User colleagueOf, params Role[] roles) =>
        await factory.SignInAsync(await factory.CreateColleagueAsync(colleagueOf, roles: roles));

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> AddBlockAsync(HttpClient http, Guid vetId, object body) =>
        http.PostAsJsonAsync($"/api/veterinarians/{vetId}/weekly-hours", body);

    private static Task<HttpResponseMessage> AddExceptionAsync(HttpClient http, object body) =>
        http.PostAsJsonAsync("/api/schedule-exceptions", body);

    // ---------- Clinic time zone ----------

    [Fact]
    public async Task ClinicTimeZone_DefaultsToUtc_AndAnAdministratorCanChangeIt()
    {
        var clinic = await ClinicAsync();

        var me = await clinic.Vet.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("UTC", me.GetProperty("clinic").GetProperty("timeZone").GetString());
        Assert.Equal("UTC", (await clinic.Vet.GetFromJsonAsync<JsonElement>("/api/clinic")).GetProperty("timeZone").GetString());

        var changed = await clinic.Admin.PatchAsJsonAsync("/api/clinic", new { timeZone = "Europe/London" });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("Europe/London", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("timeZone").GetString());
        var entry = (await factory.AuditEntriesAsync(clinic.AdminUser.ClinicId)).Last();
        Assert.Equal(AuditActions.ClinicUpdated, entry.Action);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("GMT Standard Time")]
    [InlineData("")]
    public async Task ClinicTimeZone_MustBeAKnownIanaId(string timeZone)
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Admin.PatchAsJsonAsync("/api/clinic", new { timeZone });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("timeZone", Assert.Single((await ErrorOf(response)).Details).Field);
    }

    [Fact]
    public async Task OnlyAdministrators_ChangeTheClinic()
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Vet.PatchAsJsonAsync("/api/clinic", new { timeZone = "Europe/London" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- Veterinarians ----------

    [Fact]
    public async Task Veterinarians_ListsOnlyThisClinicsActiveVets()
    {
        var clinic = await ClinicAsync();
        await factory.CreateColleagueAsync(clinic.AdminUser, UserStatus.Disabled, Role.Veterinarian);
        await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Nurse);
        await ClinicAsync(); // another clinic's vet

        var page = await clinic.Vet.GetFromJsonAsync<JsonElement>("/api/veterinarians");

        Assert.Equal([clinic.VetUser.Id], page.GetProperty("data").EnumerateArray().Select(v => v.GetProperty("id").GetGuid()));
        Assert.Equal(["id", "name", "status"], page.GetProperty("data")[0].EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData(Role.Inventory)]
    [InlineData(null)]
    public async Task UsersWithoutScheduleAccess_AreForbidden(Role? role)
    {
        var clinic = await ClinicAsync();
        var http = role is { } r ? await ColleagueAsync(clinic.AdminUser, r) : await ColleagueAsync(clinic.AdminUser);
        var vet = clinic.VetUser.Id;

        var responses = new[]
        {
            await http.GetAsync("/api/veterinarians"),
            await http.GetAsync($"/api/veterinarians/{vet}/weekly-hours"),
            await http.GetAsync($"/api/veterinarians/{vet}/working-hours?from=2026-11-02&to=2026-11-08"),
            await http.GetAsync("/api/schedule-exceptions"),
            await AddBlockAsync(http, vet, new { dayOfWeek = "Monday", startTime = "09:00", endTime = "17:00" }),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    // ---------- Weekly hours ----------

    [Fact]
    public async Task Vet_ManagesTheirOwnWeeklyHours()
    {
        var clinic = await ClinicAsync();

        var response = await AddBlockAsync(clinic.Vet, clinic.VetUser.Id, new
        {
            dayOfWeek = "Monday", startTime = "09:00", endTime = "17:30", effectiveFrom = "2026-11-01",
        });
        var created = await CreatedAsync(response);
        var id = created.GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/veterinarians/{clinic.VetUser.Id}/weekly-hours/{id}", response.Headers.Location!.ToString());

        Assert.Equal("09:00", created.GetProperty("startTime").GetString());
        Assert.Equal("17:30", created.GetProperty("endTime").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("effectiveTo").ValueKind);
        Assert.Equal(
            ["id", "veterinarianId", "dayOfWeek", "startTime", "endTime", "effectiveFrom", "effectiveTo", "createdAt", "updatedAt"],
            created.EnumerateObject().Select(p => p.Name));

        var updated = await clinic.Vet.PatchAsJsonAsync(
            $"/api/veterinarians/{clinic.VetUser.Id}/weekly-hours/{id}", new { endTime = "24:00", effectiveTo = "2026-12-31" });
        Assert.Equal("24:00", (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("endTime").GetString());

        var cleared = await clinic.Vet.PatchAsync(
            $"/api/veterinarians/{clinic.VetUser.Id}/weekly-hours/{id}", Json("""{ "effectiveTo": null }"""));
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("effectiveTo").ValueKind);

        var list = await clinic.Admin.GetFromJsonAsync<JsonElement>($"/api/veterinarians/{clinic.VetUser.Id}/weekly-hours");
        Assert.Equal([id], list.GetProperty("data").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()));

        var deleted = await clinic.Vet.DeleteAsync($"/api/veterinarians/{clinic.VetUser.Id}/weekly-hours/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(
            [AuditActions.WorkingHoursCreated, AuditActions.WorkingHoursUpdated, AuditActions.WorkingHoursUpdated, AuditActions.WorkingHoursDeleted],
            (await factory.AuditEntriesAsync(id)).Select(e => e.Action));
    }

    [Fact]
    public async Task Vets_CannotChangeAnotherVetsHours_ButAdministratorsCan()
    {
        var clinic = await ClinicAsync();
        var otherVet = await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Veterinarian);
        var nurse = await ColleagueAsync(clinic.AdminUser, Role.Nurse);
        var body = new { dayOfWeek = "Tuesday", startTime = "09:00", endTime = "12:00" };

        Assert.Equal(HttpStatusCode.Forbidden, (await AddBlockAsync(clinic.Vet, otherVet.Id, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AddBlockAsync(nurse, clinic.VetUser.Id, body)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await nurse.GetAsync($"/api/veterinarians/{clinic.VetUser.Id}/weekly-hours")).StatusCode);
        await CreatedAsync(await AddBlockAsync(clinic.Admin, otherVet.Id, body));
    }

    [Fact]
    public async Task Vets_CannotTouchAnotherVetsExceptionOrAClosure()
    {
        var clinic = await ClinicAsync();
        var otherVet = await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Veterinarian);
        async Task<Guid> Add(Guid? vet, string type) =>
            (await CreatedAsync(await AddExceptionAsync(clinic.Admin, new
            {
                veterinarianId = vet, type, startsAt = "2026-11-02T00:00:00Z", endsAt = "2026-11-03T00:00:00Z",
            }))).GetProperty("id").GetGuid();
        var theirs = await Add(otherVet.Id, "Leave");
        var closure = await Add(null, "Closure");

        foreach (var id in new[] { theirs, closure })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Vet.PatchAsJsonAsync($"/api/schedule-exceptions/{id}", new { reason = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Vet.DeleteAsync($"/api/schedule-exceptions/{id}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await clinic.Admin.GetAsync($"/api/schedule-exceptions/{theirs}")).StatusCode);
    }

    [Fact]
    public async Task AVetsOwnIdWithAnotherVetsBlock_IsNotFound()
    {
        var clinic = await ClinicAsync();
        var otherVet = await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Veterinarian);
        var theirBlock = (await CreatedAsync(await AddBlockAsync(clinic.Admin, otherVet.Id, new { dayOfWeek = "Monday", startTime = "09:00", endTime = "17:00" })))
            .GetProperty("id").GetGuid();

        var patch = await clinic.Vet.PatchAsJsonAsync($"/api/veterinarians/{clinic.VetUser.Id}/weekly-hours/{theirBlock}", new { endTime = "18:00" });
        var delete = await clinic.Vet.DeleteAsync($"/api/veterinarians/{clinic.VetUser.Id}/weekly-hours/{theirBlock}");

        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        var list = await clinic.Admin.GetFromJsonAsync<JsonElement>($"/api/veterinarians/{otherVet.Id}/weekly-hours");
        Assert.Equal("17:00", list.GetProperty("data")[0].GetProperty("endTime").GetString());
    }

    [Fact]
    public async Task AnotherClinicsVetAndBlocks_AreNotFound_OnEveryPath()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var theirVet = other.VetUser.Id;
        var theirBlock = (await CreatedAsync(await AddBlockAsync(other.Admin, theirVet, new { dayOfWeek = "Monday", startTime = "09:00", endTime = "17:00" })))
            .GetProperty("id").GetGuid();

        var responses = new[]
        {
            await clinic.Admin.PatchAsJsonAsync($"/api/veterinarians/{theirVet}/weekly-hours/{theirBlock}", new { endTime = "18:00" }),
            await clinic.Admin.DeleteAsync($"/api/veterinarians/{theirVet}/weekly-hours/{theirBlock}"),
            await clinic.Admin.GetAsync($"/api/veterinarians/{theirVet}/working-hours?from=2026-11-02&to=2026-11-08"),
            await clinic.Admin.PatchAsJsonAsync($"/api/veterinarians/{clinic.VetUser.Id}/weekly-hours/{theirBlock}", new { endTime = "18:00" }),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    [Fact]
    public async Task NoPermission_IsForbiddenBeforeBodyValidation()
    {
        var clinic = await ClinicAsync();
        var nurse = await ColleagueAsync(clinic.AdminUser, Role.Nurse);

        var response = await AddBlockAsync(nurse, clinic.VetUser.Id, new { dayOfWeek = "Monday" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OverlappingBlocks_AreRejected_UnlessTheirDatesDoNotOverlap()
    {
        var clinic = await ClinicAsync();
        var vet = clinic.VetUser.Id;
        await CreatedAsync(await AddBlockAsync(clinic.Admin, vet, new
        {
            dayOfWeek = "Monday", startTime = "09:00", endTime = "13:00", effectiveFrom = "2026-01-01", effectiveTo = "2026-06-30",
        }));

        var overlapping = await AddBlockAsync(clinic.Admin, vet, new
        {
            dayOfWeek = "Monday", startTime = "12:00", endTime = "17:00", effectiveFrom = "2026-06-01",
        });
        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);

        await CreatedAsync(await AddBlockAsync(clinic.Admin, vet, new
        {
            dayOfWeek = "Monday", startTime = "12:00", endTime = "17:00", effectiveFrom = "2026-07-01",
        }));
        await CreatedAsync(await AddBlockAsync(clinic.Admin, vet, new
        {
            dayOfWeek = "Monday", startTime = "13:00", endTime = "17:00", effectiveFrom = "2026-01-01", effectiveTo = "2026-06-30",
        }));
        await CreatedAsync(await AddBlockAsync(clinic.Admin, vet, new { dayOfWeek = "Tuesday", startTime = "12:00", endTime = "17:00" }));
    }

    [Theory]
    [InlineData("""{ "dayOfWeek": "Monday", "startTime": "09:07", "endTime": "17:00" }""", "startTime")]
    [InlineData("""{ "dayOfWeek": "Monday", "startTime": "9:00", "endTime": "17:00" }""", "startTime")]
    [InlineData("""{ "dayOfWeek": "Monday", "startTime": "24:00", "endTime": "24:00" }""", "startTime")]
    [InlineData("""{ "dayOfWeek": "Monday", "startTime": "09:00", "endTime": "24:05" }""", "endTime")]
    [InlineData("""{ "dayOfWeek": "Monday", "startTime": "17:00", "endTime": "09:00" }""", "endTime")]
    [InlineData("""{ "dayOfWeek": "Monday", "startTime": "09:00", "endTime": "17:00", "effectiveFrom": "2026-05-01", "effectiveTo": "2026-04-30" }""", "effectiveTo")]
    [InlineData("""{ "startTime": "09:00", "endTime": "17:00" }""", "dayOfWeek")]
    [InlineData("""{ "dayOfWeek": "Monday", "startTime": "\u0660\u0669:\u0660\u0660", "endTime": "17:00" }""", "startTime")]
    [InlineData("""{ "dayOfWeek": "Monday", "startTime": "09:00", "endTime": "17:00", "effectiveFrom": "0001-01-01" }""", "effectiveFrom")]
    public async Task InvalidBlocks_Are422(string json, string field)
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Admin.PostAsync($"/api/veterinarians/{clinic.VetUser.Id}/weekly-hours", Json(json));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, d => d.Field == field);
    }

    [Fact]
    public async Task UnknownDayName_Is400()
    {
        var clinic = await ClinicAsync();

        var response = await AddBlockAsync(clinic.Admin, clinic.VetUser.Id, new { dayOfWeek = "monday", startTime = "09:00", endTime = "10:00" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NonVets_DisabledVets_AndOtherClinicsVets()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var nurse = await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Nurse);
        var disabled = await factory.CreateColleagueAsync(clinic.AdminUser, UserStatus.Disabled, Role.Veterinarian);
        var body = new { dayOfWeek = "Monday", startTime = "09:00", endTime = "10:00" };

        Assert.Equal(HttpStatusCode.NotFound, (await AddBlockAsync(clinic.Admin, nurse.Id, body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AddBlockAsync(clinic.Admin, other.VetUser.Id, body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Admin.GetAsync($"/api/veterinarians/{other.VetUser.Id}/weekly-hours")).StatusCode);
        var disabledResponse = await AddBlockAsync(clinic.Admin, disabled.Id, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, disabledResponse.StatusCode);
        Assert.Equal("veterinarianId", Assert.Single((await ErrorOf(disabledResponse)).Details).Field);
    }

    // ---------- Exceptions and closures ----------

    [Fact]
    public async Task ExceptionDetails_AreVisibleOnlyToManagersAndTheVetConcerned()
    {
        var clinic = await ClinicAsync();
        var nurse = await ColleagueAsync(clinic.AdminUser, Role.Nurse);
        var otherVet = await ColleagueAsync(clinic.AdminUser, Role.Veterinarian);
        var created = await CreatedAsync(await AddExceptionAsync(clinic.Vet, new
        {
            veterinarianId = clinic.VetUser.Id, type = "Sickness", startsAt = "2026-11-02T00:00:00Z", endsAt = "2026-11-03T00:00:00Z",
            reason = "Flu",
        }));
        var id = created.GetProperty("id").GetGuid();

        foreach (var (http, sees) in new[] { (clinic.Vet, true), (clinic.Admin, true), (nurse, false), (otherVet, false) })
        {
            var single = await http.GetFromJsonAsync<JsonElement>($"/api/schedule-exceptions/{id}");
            var listed = (await http.GetFromJsonAsync<JsonElement>("/api/schedule-exceptions")).GetProperty("data")[0];
            foreach (var view in new[] { single, listed })
            {
                Assert.Equal(sees ? "Sickness" : "Unavailable", view.GetProperty("type").GetString());
                Assert.Equal(sees ? "Flu" : null, view.GetProperty("reason").GetString());
            }
        }

        var entry = Assert.Single(await factory.AuditEntriesAsync(id));
        Assert.DoesNotContain("Flu", entry.Metadata ?? "");
        Assert.DoesNotContain("Sickness", entry.Metadata ?? "");
    }

    [Fact]
    public async Task Closures_AreClinicWide_AdministratorOnly_AndVisibleToAll()
    {
        var clinic = await ClinicAsync();
        var nurse = await ColleagueAsync(clinic.AdminUser, Role.Nurse);
        var closure = new { type = "Closure", startsAt = "2026-12-25T00:00:00Z", endsAt = "2026-12-26T00:00:00Z", reason = "Christmas" };

        Assert.Equal(HttpStatusCode.Forbidden, (await AddExceptionAsync(clinic.Vet, closure)).StatusCode);
        await CreatedAsync(await AddExceptionAsync(clinic.Admin, closure));

        var seen = (await nurse.GetFromJsonAsync<JsonElement>("/api/schedule-exceptions")).GetProperty("data")[0];
        Assert.Equal("Closure", seen.GetProperty("type").GetString());
        Assert.Equal("Christmas", seen.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("veterinarianId").ValueKind);
    }

    [Theory]
    [InlineData("""{ "type": "Leave", "startsAt": "2026-11-02T09:00:00Z", "endsAt": "2026-11-02T10:00:00Z" }""", "veterinarianId")]
    [InlineData("""{ "veterinarianId": "VET", "type": "Closure", "startsAt": "2026-11-02T09:00:00Z", "endsAt": "2026-11-02T10:00:00Z" }""", "type")]
    [InlineData("""{ "veterinarianId": "VET", "type": "Leave", "startsAt": "2026-11-02T09:02:00Z", "endsAt": "2026-11-02T10:00:00Z" }""", "startsAt")]
    [InlineData("""{ "veterinarianId": "VET", "type": "Leave", "startsAt": "2026-11-02T10:00:00Z", "endsAt": "2026-11-02T10:00:00Z" }""", "endsAt")]
    [InlineData("""{ "veterinarianId": "VET", "type": "Leave", "startsAt": "2026-11-02T09:00:00Z" }""", "endsAt")]
    [InlineData("""{ "veterinarianId": "VET", "type": "Leave", "startsAt": "0001-01-01T00:00:00Z", "endsAt": "2026-11-02T10:00:00Z" }""", "startsAt")]
    [InlineData("""{ "veterinarianId": "VET", "type": "Leave", "startsAt": "2026-11-02T09:00:00Z", "endsAt": "9999-12-31T00:00:00Z" }""", "endsAt")]
    public async Task InvalidExceptions_Are422(string json, string field)
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Admin.PostAsync("/api/schedule-exceptions", Json(json.Replace("VET", clinic.VetUser.Id.ToString())));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, d => d.Field == field);
    }

    [Fact]
    public async Task UnavailableIsNotAnInputType_Is400()
    {
        var clinic = await ClinicAsync();

        var response = await AddExceptionAsync(clinic.Admin, new
        {
            veterinarianId = clinic.VetUser.Id, type = "Unavailable", startsAt = "2026-11-02T00:00:00Z", endsAt = "2026-11-03T00:00:00Z",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("2026-11-02T09:00:00")]
    [InlineData("2026-11-02")]
    [InlineData("02/11/2026 09:00")]
    public async Task InstantsWithoutAnExplicitOffset_Are400(string startsAt)
    {
        var clinic = await ClinicAsync();

        var response = await AddExceptionAsync(clinic.Admin, new
        {
            veterinarianId = clinic.VetUser.Id, type = "Leave", startsAt, endsAt = "2026-11-03T00:00:00Z",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExceptionList_FiltersByVetAndPeriod_AndAlwaysIncludesClosures()
    {
        var clinic = await ClinicAsync();
        var otherVet = await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Veterinarian);
        async Task<Guid> Add(Guid? vet, string type, string start, string end) =>
            (await CreatedAsync(await AddExceptionAsync(clinic.Admin, new { veterinarianId = vet, type, startsAt = start, endsAt = end })))
                .GetProperty("id").GetGuid();
        var mine = await Add(clinic.VetUser.Id, "Leave", "2026-11-02T00:00:00Z", "2026-11-03T00:00:00Z");
        await Add(otherVet.Id, "Training", "2026-11-02T00:00:00Z", "2026-11-03T00:00:00Z");
        var closure = await Add(null, "Closure", "2026-11-05T00:00:00Z", "2026-11-06T00:00:00Z");
        var later = await Add(clinic.VetUser.Id, "Leave", "2026-12-01T00:00:00Z", "2026-12-02T00:00:00Z");

        async Task<List<Guid>> Ids(string query) =>
            (await clinic.Admin.GetFromJsonAsync<JsonElement>("/api/schedule-exceptions?" + query)).GetProperty("data")
                .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([mine, closure, later], await Ids($"veterinarianId={clinic.VetUser.Id}"));
        Assert.Equal([mine, closure], await Ids($"veterinarianId={clinic.VetUser.Id}&from=2026-11-01T00:00:00Z&to=2026-11-30T00:00:00%2B01:00"));

        var bad = await clinic.Admin.GetAsync("/api/schedule-exceptions?from=2026-11-01");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("from", Assert.Single((await ErrorOf(bad)).Details).Field);
    }

    [Fact]
    public async Task Exceptions_CanBeMovedAndCancelled_ByTheVet()
    {
        var clinic = await ClinicAsync();
        var id = (await CreatedAsync(await AddExceptionAsync(clinic.Vet, new
        {
            veterinarianId = clinic.VetUser.Id, type = "Leave", startsAt = "2026-11-02T00:00:00Z", endsAt = "2026-11-03T00:00:00Z",
        }))).GetProperty("id").GetGuid();

        var moved = await clinic.Vet.PatchAsJsonAsync($"/api/schedule-exceptions/{id}", new
        {
            startsAt = "2026-11-09T00:00:00+01:00", endsAt = "2026-11-10T00:00:00+01:00",
        });
        var body = await moved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(DateTimeOffset.Parse("2026-11-08T23:00:00Z"), body.GetProperty("startsAt").GetDateTimeOffset());

        var cleared = await clinic.Vet.PatchAsJsonAsync($"/api/schedule-exceptions/{id}", new { reason = "" });
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").ValueKind);

        var toClosure = await clinic.Admin.PatchAsJsonAsync($"/api/schedule-exceptions/{id}", new { type = "Closure" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, toClosure.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await clinic.Vet.DeleteAsync($"/api/schedule-exceptions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/schedule-exceptions/{id}")).StatusCode);
    }

    [Fact]
    public async Task OtherClinicsExceptions_AreNotFound()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var theirs = (await CreatedAsync(await AddExceptionAsync(other.Admin, new
        {
            type = "Closure", startsAt = "2026-12-25T00:00:00Z", endsAt = "2026-12-26T00:00:00Z",
        }))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Admin.GetAsync($"/api/schedule-exceptions/{theirs}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Admin.PatchAsJsonAsync($"/api/schedule-exceptions/{theirs}", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Admin.DeleteAsync($"/api/schedule-exceptions/{theirs}")).StatusCode);
        Assert.Empty((await clinic.Admin.GetFromJsonAsync<JsonElement>("/api/schedule-exceptions")).GetProperty("data").EnumerateArray());
    }

    // ---------- Computed working hours ----------

    [Fact]
    public async Task WorkingHours_AreWeeklyHoursMinusExceptionsAndClosures_InTheClinicTimeZone()
    {
        var clinic = await ClinicAsync();
        var vet = clinic.VetUser.Id;
        await clinic.Admin.PatchAsJsonAsync("/api/clinic", new { timeZone = "Europe/London" });
        foreach (var day in new[] { "Monday", "Tuesday" })
        {
            await CreatedAsync(await AddBlockAsync(clinic.Vet, vet, new { dayOfWeek = day, startTime = "09:00", endTime = "17:00", effectiveFrom = "2026-01-01" }));
        }

        // Monday 2026-06-01 (London is UTC+1): leave 12:00–13:00 local. Tuesday: clinic closed all day.
        await CreatedAsync(await AddExceptionAsync(clinic.Vet, new
        {
            veterinarianId = vet, type = "Leave", startsAt = "2026-06-01T12:00:00+01:00", endsAt = "2026-06-01T13:00:00+01:00",
        }));
        await CreatedAsync(await AddExceptionAsync(clinic.Admin, new
        {
            type = "Closure", startsAt = "2026-06-02T00:00:00+01:00", endsAt = "2026-06-03T00:00:00+01:00",
        }));

        var hours = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/veterinarians/{vet}/working-hours?from=2026-06-01&to=2026-06-07");

        Assert.Equal("Europe/London", hours.GetProperty("timeZone").GetString());
        Assert.Equal(
            ["2026-06-01T09:00:00+01:00/2026-06-01T12:00:00+01:00", "2026-06-01T13:00:00+01:00/2026-06-01T17:00:00+01:00"],
            hours.GetProperty("intervals").EnumerateArray()
                .Select(i => i.GetProperty("startAt").GetString() + "/" + i.GetProperty("endAt").GetString()));
    }

    [Theory]
    [InlineData("from=2026-06-01", "to")]
    [InlineData("from=2026-06-08&to=2026-06-01", "to")]
    [InlineData("from=2026-01-01&to=2026-03-04", "to")]
    [InlineData("from=1/6/2026&to=2026-06-07", "from")]
    [InlineData("from=0001-01-01&to=0001-01-02", "from")]
    [InlineData("from=9999-12-30&to=9999-12-31", "to")]
    public async Task WorkingHours_ValidateTheRange(string query, string field)
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Vet.GetAsync($"/api/veterinarians/{clinic.VetUser.Id}/working-hours?{query}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, d => d.Field == field);
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE schedule_exceptions SET veterinarian_id = NULL WHERE id = {0}")]
    [InlineData("UPDATE schedule_exceptions SET starts_at = starts_at + interval '1 minute' WHERE id = {0}")]
    [InlineData("UPDATE schedule_exceptions SET ends_at = starts_at WHERE id = {0}")]
    [InlineData("UPDATE schedule_exceptions SET type = 'Closure' WHERE id = {0}")]
    [InlineData("UPDATE schedule_exceptions SET starts_at = starts_at + interval '1 microsecond' WHERE id = {0}")]
    public async Task Database_RejectsInvalidExceptions(string sql)
    {
        var clinic = await ClinicAsync();
        var id = (await CreatedAsync(await AddExceptionAsync(clinic.Admin, new
        {
            veterinarianId = clinic.VetUser.Id, type = "Leave", startsAt = "2026-11-02T00:00:00Z", endsAt = "2026-11-03T00:00:00Z",
        }))).GetProperty("id").GetGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Theory]
    [InlineData("UPDATE working_hours SET start_minute = 3 WHERE id = {0}", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE working_hours SET end_minute = 1445 WHERE id = {0}", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE working_hours SET day_of_week = 'Funday' WHERE id = {0}", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE working_hours SET effective_to = effective_from - 1 WHERE id = {0}", PostgresErrorCodes.CheckViolation)]
    public async Task Database_RejectsInvalidBlocks(string sql, string sqlState)
    {
        var clinic = await ClinicAsync();
        var id = (await CreatedAsync(await AddBlockAsync(clinic.Admin, clinic.VetUser.Id, new { dayOfWeek = "Monday", startTime = "09:00", endTime = "17:00" })))
            .GetProperty("id").GetGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal(sqlState, exception.SqlState);
    }

    [Fact]
    public async Task Database_RefusesABlockForAnotherClinicsUser()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var id = (await CreatedAsync(await AddBlockAsync(clinic.Admin, clinic.VetUser.Id, new { dayOfWeek = "Monday", startTime = "09:00", endTime = "17:00" })))
            .GetProperty("id").GetGuid();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE working_hours SET veterinarian_id = {0} WHERE id = {1}", other.VetUser.Id, id)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }
}
