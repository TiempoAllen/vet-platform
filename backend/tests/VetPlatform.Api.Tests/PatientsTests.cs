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

/// <summary>Patient records, ownership, permissions and tenant isolation (B-06) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class PatientsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<Guid> CreateClientAsync(HttpClient http, string firstName = "Jane", string lastName = "Doe")
    {
        var response = await http.PostAsJsonAsync("/api/clients", new { firstName, lastName });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> CreatePatientAsync(HttpClient http, object body)
    {
        var response = await http.PostAsJsonAsync("/api/patients", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CreatePatientIdAsync(HttpClient http, Guid clientId, string name = "Rex", string species = "Dog") =>
        (await CreatePatientAsync(http, new { clientId, name, species })).GetProperty("id").GetGuid();

    /// <summary>A receptionist (clients and patients) in a new clinic, with one client.</summary>
    private async Task<(HttpClient Http, User User, Guid ClientId)> ReceptionWithClientAsync()
    {
        var (http, user) = await factory.SignedInAsync(Role.Receptionist);
        return (http, user, await CreateClientAsync(http));
    }

    private static async Task<List<string>> ListNamesAsync(HttpClient http, string query)
    {
        var page = await http.GetFromJsonAsync<JsonElement>("/api/patients?" + query);
        return page.GetProperty("data").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToList();
    }

    // ---------- Permissions ----------

    [Theory]
    [InlineData(Role.Inventory)]
    [InlineData(null)]
    public async Task UsersWithoutPatientAccess_CannotReadOrManagePatients(Role? role)
    {
        var (http, _) = await factory.SignedInAsync(role is { } r ? [r] : []);
        var id = Guid.NewGuid();

        var responses = new[]
        {
            await http.GetAsync("/api/patients"),
            await http.GetAsync($"/api/patients/{id}"),
            await http.PostAsJsonAsync("/api/patients", new { clientId = id, name = "Rex", species = "Dog" }),
            await http.PatchAsJsonAsync($"/api/patients/{id}", new { name = "x" }),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(ErrorCodes.Forbidden, (await ErrorOf(response)).Code);
        }
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.Veterinarian)]
    [InlineData(Role.Nurse)]
    [InlineData(Role.Receptionist)]
    public async Task ClinicStaff_CanReadAndManagePatients(Role role)
    {
        var (reception, receptionist, clientId) = await ReceptionWithClientAsync();
        var colleague = await factory.CreateColleagueAsync(receptionist, roles: role);
        var http = await factory.SignInAsync(colleague);

        var id = await CreatePatientIdAsync(http, clientId);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/patients/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/patients")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PatchAsJsonAsync($"/api/patients/{id}", new { name = "Max" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reception.GetAsync($"/api/patients/{id}")).StatusCode);
    }

    [Fact]
    public async Task Me_ListsPatientPermissions()
    {
        var (http, _) = await factory.SignedInAsync(Role.Veterinarian);

        var me = await http.GetFromJsonAsync<JsonElement>("/api/auth/me");

        Assert.Equal(
            [
                Permissions.AppointmentsManage, Permissions.AppointmentsRead, Permissions.ClientsRead, Permissions.PatientsManage,
                Permissions.PatientsRead, Permissions.SchedulesManageOwn, Permissions.SchedulesRead,
            ],
            me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    // ---------- Tenant isolation ----------

    [Fact]
    public async Task Staff_OnlyEverSeeTheirOwnClinicsPatients()
    {
        var (httpA, _, clientA) = await ReceptionWithClientAsync();
        var (httpB, _, clientB) = await ReceptionWithClientAsync();
        var mine = await CreatePatientIdAsync(httpA, clientA, "Shared Name");
        await CreatePatientIdAsync(httpB, clientB, "Shared Name");

        var page = await httpA.GetFromJsonAsync<JsonElement>("/api/patients?q=shared");

        Assert.Equal([mine], page.GetProperty("data").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task OtherClinicsPatients_AreNotFound_AndUnchanged()
    {
        var (httpA, _, _) = await ReceptionWithClientAsync();
        var (httpB, _, clientB) = await ReceptionWithClientAsync();
        var theirs = await CreatePatientIdAsync(httpB, clientB);

        var responses = new[]
        {
            await httpA.GetAsync($"/api/patients/{theirs}"),
            await httpA.PatchAsJsonAsync($"/api/patients/{theirs}", new { name = "Hijacked", status = "Inactive" }),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var untouched = await httpB.GetFromJsonAsync<JsonElement>($"/api/patients/{theirs}");
        Assert.Equal("Rex", untouched.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AnotherClinicsClient_CannotBeChosenAsOwner_OnCreateOrTransfer()
    {
        var (httpA, _, clientA) = await ReceptionWithClientAsync();
        var (_, _, clientB) = await ReceptionWithClientAsync();
        var patient = await CreatePatientIdAsync(httpA, clientA);

        var create = await httpA.PostAsJsonAsync("/api/patients", new { clientId = clientB, name = "Rex", species = "Dog" });
        var transfer = await httpA.PatchAsJsonAsync($"/api/patients/{patient}", new { clientId = clientB });
        var unknown = await httpA.PostAsJsonAsync("/api/patients", new { clientId = Guid.NewGuid(), name = "Rex", species = "Dog" });

        foreach (var response in new[] { create, transfer, unknown })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var error = await ErrorOf(response);
            Assert.Equal("clientId", Assert.Single(error.Details).Field);
            Assert.Equal("The client was not found or is not active.", error.Details[0].Message);
        }
    }

    [Fact]
    public async Task Database_RefusesAPatientLinkedToAnotherClinicsClient()
    {
        var (httpA, _, clientA) = await ReceptionWithClientAsync();
        var (_, _, clientB) = await ReceptionWithClientAsync();
        var patient = await CreatePatientIdAsync(httpA, clientA);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE patients SET client_id = {0} WHERE id = {1}", clientB, patient)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    // ---------- Create / read ----------

    [Fact]
    public async Task Create_Returns201WithLocation_AndNormalisesInput()
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();

        var response = await http.PostAsJsonAsync("/api/patients", new
        {
            clientId, name = "  Rex ", species = "Dog", breed = " Labrador ", sex = "Male", reproductiveStatus = "Neutered",
            dateOfBirth = "2020-05-01", dateOfBirthEstimated = true, markings = "Black, white chest",
            microchipNumber = "985 112-003.45678a", weightKg = 31.25m, notes = "Friendly.",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/api/patients/{id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal("Rex", body.GetProperty("name").GetString());
        Assert.Equal("Labrador", body.GetProperty("breed").GetString());
        Assert.Equal("2020-05-01", body.GetProperty("dateOfBirth").GetString());
        Assert.True(body.GetProperty("dateOfBirthEstimated").GetBoolean());
        Assert.Equal("98511200345678A", body.GetProperty("microchipNumber").GetString());
        Assert.Equal(31.25m, body.GetProperty("weightKg").GetDecimal());
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.Equal(clientId, body.GetProperty("client").GetProperty("id").GetGuid());
        Assert.Equal("Jane", body.GetProperty("client").GetProperty("firstName").GetString());

        var fetched = await http.GetFromJsonAsync<JsonElement>($"/api/patients/{id}");
        Assert.Equal(body.ToString(), fetched.ToString());
    }

    [Fact]
    public async Task Create_WithOnlyRequiredFields_UsesDefaults()
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();

        var body = await CreatePatientAsync(http, new { clientId, name = "Tom", species = "Cat", dateOfBirthEstimated = true });

        Assert.Equal("Unknown", body.GetProperty("sex").GetString());
        Assert.Equal("Unknown", body.GetProperty("reproductiveStatus").GetString());
        Assert.False(body.GetProperty("dateOfBirthEstimated").GetBoolean()); // no date, so no estimate
        foreach (var field in new[] { "breed", "dateOfBirth", "markings", "microchipNumber", "weightKg", "dateOfDeath", "notes" })
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty(field).ValueKind);
        }
    }

    [Theory]
    [InlineData("""{ "name": "Rex", "species": "Dog" }""", "clientId")]
    [InlineData("""{ "clientId": "CLIENT", "species": "Dog" }""", "name")]
    [InlineData("""{ "clientId": "CLIENT", "name": " ", "species": "Dog" }""", "name")]
    [InlineData("""{ "clientId": "CLIENT", "name": "Rex" }""", "species")]
    [InlineData("""{ "clientId": "CLIENT", "name": "Rex", "species": "Dog", "microchipNumber": "ABC!123" }""", "microchipNumber")]
    [InlineData("""{ "clientId": "CLIENT", "name": "Rex", "species": "Dog", "microchipNumber": "123456789012345678901" }""", "microchipNumber")]
    [InlineData("""{ "clientId": "CLIENT", "name": "Rex", "species": "Dog", "weightKg": 0 }""", "weightKg")]
    [InlineData("""{ "clientId": "CLIENT", "name": "Rex", "species": "Dog", "weightKg": 2000.001 }""", "weightKg")]
    [InlineData("""{ "clientId": "CLIENT", "name": "Rex", "species": "Dog", "weightKg": 1.2345 }""", "weightKg")]
    [InlineData("""{ "clientId": "CLIENT", "name": "Rex", "species": "Dog", "dateOfBirth": "2999-01-01" }""", "dateOfBirth")]
    public async Task Create_RejectsInvalidFields_With422(string json, string field)
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();

        var response = await http.PostAsync("/api/patients", Json(json.Replace("CLIENT", clientId.ToString())));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, detail => detail.Field == field);
    }

    [Fact]
    public async Task Create_ReportsServerSideProblemsTogether()
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();

        var response = await http.PostAsJsonAsync("/api/patients", new
        {
            clientId, name = "Rex", species = "Dog", weightKg = -1, dateOfBirth = "2999-01-01", microchipNumber = "??",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            ["dateOfBirth", "microchipNumber", "weightKg"],
            (await ErrorOf(response)).Details.Select(detail => detail.Field).Order());
    }

    [Theory]
    [InlineData("""{ "species": "dog" }""")]
    [InlineData("""{ "species": "Dog", "sex": "M" }""")]
    [InlineData("""{ "species": "Dog", "reproductiveStatus": "Spayed" }""")]
    public async Task Create_RejectsUnknownEnumNames_With400(string json)
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();

        var body = json.Replace("{ ", $$"""{ "clientId": "{{clientId}}", "name": "Rex", """);
        var response = await http.PostAsync("/api/patients", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForAnInactiveClient_Is422()
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();
        await http.PatchAsJsonAsync($"/api/clients/{clientId}", new { status = "Inactive" });

        var response = await http.PostAsJsonAsync("/api/patients", new { clientId, name = "Rex", species = "Dog" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("clientId", Assert.Single((await ErrorOf(response)).Details).Field);
    }

    [Fact]
    public async Task Microchips_AreUniqueWithinAClinic_ButNotAcrossClinics()
    {
        var (httpA, _, clientA) = await ReceptionWithClientAsync();
        var (httpB, _, clientB) = await ReceptionWithClientAsync();
        await CreatePatientAsync(httpA, new { clientId = clientA, name = "Rex", species = "Dog", microchipNumber = "985112003456789" });

        var duplicate = await httpA.PostAsJsonAsync(
            "/api/patients", new { clientId = clientA, name = "Rex 2", species = "Dog", microchipNumber = "985-112-003-456-789" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("microchipNumber", Assert.Single((await ErrorOf(duplicate)).Details).Field);

        await CreatePatientAsync(httpB, new { clientId = clientB, name = "Rex", species = "Dog", microchipNumber = "985112003456789" });
    }

    [Fact]
    public async Task Patients_CannotBeDeleted()
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();
        var id = await CreatePatientIdAsync(http, clientId);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.DeleteAsync($"/api/patients/{id}")).StatusCode);
    }

    // ---------- Update ----------

    [Fact]
    public async Task Update_ChangesOnlyGivenFields_AndExplicitNullClearsDatesAndWeight()
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();
        var id = (await CreatePatientAsync(http, new
        {
            clientId, name = "Rex", species = "Dog", breed = "Lab", dateOfBirth = "2020-01-01", dateOfBirthEstimated = true,
            weightKg = 30, microchipNumber = "123",
        })).GetProperty("id").GetGuid();

        var response = await http.PatchAsync($"/api/patients/{id}", Json("""{ "breed": "", "weightKg": null, "dateOfBirth": null, "microchipNumber": "" }"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rex", body.GetProperty("name").GetString());
        Assert.Equal("Dog", body.GetProperty("species").GetString());
        foreach (var field in new[] { "breed", "weightKg", "dateOfBirth", "microchipNumber" })
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty(field).ValueKind);
        }

        Assert.False(body.GetProperty("dateOfBirthEstimated").GetBoolean());
    }

    [Fact]
    public async Task Update_OmittedNullableFields_AreUnchanged()
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();
        var id = (await CreatePatientAsync(http, new { clientId, name = "Rex", species = "Dog", dateOfBirth = "2020-01-01", weightKg = 30 }))
            .GetProperty("id").GetGuid();

        var body = await (await http.PatchAsJsonAsync($"/api/patients/{id}", new { name = "Max" }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("2020-01-01", body.GetProperty("dateOfBirth").GetString());
        Assert.Equal(30m, body.GetProperty("weightKg").GetDecimal());
    }

    [Fact]
    public async Task Transfer_ToAnotherActiveClient_ChangesTheOwner_AndIsAudited()
    {
        var (http, _, oldOwner) = await ReceptionWithClientAsync();
        var newOwner = await CreateClientAsync(http, "John", "Smith");
        var id = await CreatePatientIdAsync(http, oldOwner);

        var response = await http.PatchAsJsonAsync($"/api/patients/{id}", new { clientId = newOwner });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var client = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client");
        Assert.Equal(newOwner, client.GetProperty("id").GetGuid());
        Assert.Equal("Smith", client.GetProperty("lastName").GetString());

        var entry = (await factory.AuditEntriesAsync(id)).Last();
        Assert.Equal(AuditActions.PatientUpdated, entry.Action);
        var metadata = JsonDocument.Parse(entry.Metadata!).RootElement;
        Assert.Equal(oldOwner, metadata.GetProperty("fromClientId").GetGuid());
        Assert.Equal(newOwner, metadata.GetProperty("toClientId").GetGuid());
    }

    [Fact]
    public async Task Transfer_ToAnInactiveClient_Is422_ButAnInactiveCurrentOwnerDoesNotBlockEdits()
    {
        var (http, _, owner) = await ReceptionWithClientAsync();
        var inactive = await CreateClientAsync(http, "Gone", "Away");
        await http.PatchAsJsonAsync($"/api/clients/{inactive}", new { status = "Inactive" });
        var id = await CreatePatientIdAsync(http, owner);

        var transfer = await http.PatchAsJsonAsync($"/api/patients/{id}", new { clientId = inactive });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, transfer.StatusCode);

        await http.PatchAsJsonAsync($"/api/clients/{owner}", new { status = "Inactive" });
        var edit = await http.PatchAsJsonAsync($"/api/patients/{id}", new { clientId = owner, name = "Max" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
    }

    [Fact]
    public async Task Deceased_RecordsADateOfDeath_AndLeavingDeceasedClearsIt()
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();
        var id = (await CreatePatientAsync(http, new { clientId, name = "Rex", species = "Dog", dateOfBirth = "2010-01-01" }))
            .GetProperty("id").GetGuid();

        var deceased = await http.PatchAsJsonAsync($"/api/patients/{id}", new { status = "Deceased", dateOfDeath = "2024-03-01" });
        var body = await deceased.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Deceased", body.GetProperty("status").GetString());
        Assert.Equal("2024-03-01", body.GetProperty("dateOfDeath").GetString());

        var corrected = await http.PatchAsJsonAsync($"/api/patients/{id}", new { status = "Active" });
        Assert.Equal(JsonValueKind.Null, (await corrected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dateOfDeath").ValueKind);
    }

    [Theory]
    [InlineData("""{ "dateOfDeath": "2024-03-01" }""")]
    [InlineData("""{ "status": "Deceased", "dateOfDeath": "2009-12-31" }""")]
    [InlineData("""{ "status": "Deceased", "dateOfDeath": "2999-01-01" }""")]
    public async Task Update_RejectsInvalidDatesOfDeath(string json)
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();
        var id = (await CreatePatientAsync(http, new { clientId, name = "Rex", species = "Dog", dateOfBirth = "2010-01-01" }))
            .GetProperty("id").GetGuid();

        var response = await http.PatchAsync($"/api/patients/{id}", Json(json));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("dateOfDeath", Assert.Single((await ErrorOf(response)).Details).Field);
    }

    [Fact]
    public async Task Update_ToAMicrochipAnotherPatientHas_Is409()
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();
        await CreatePatientAsync(http, new { clientId, name = "Rex", species = "Dog", microchipNumber = "111" });
        var other = await CreatePatientIdAsync(http, clientId, "Max");

        var response = await http.PatchAsJsonAsync($"/api/patients/{other}", new { microchipNumber = "1-1-1" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Update_IgnoresIdClinicAndTimestampsInTheBody()
    {
        var (http, user, clientId) = await ReceptionWithClientAsync();
        var (_, other) = await factory.SignedInAsync(Role.Receptionist);
        var created = await CreatePatientAsync(http, new { clientId, name = "Rex", species = "Dog" });
        var id = created.GetProperty("id").GetGuid();

        var response = await http.PatchAsJsonAsync($"/api/patients/{id}", new
        {
            id = Guid.NewGuid(), clinicId = other.ClinicId, createdAt = "2000-01-01T00:00:00+00:00", name = "Max",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal(created.GetProperty("createdAt").GetString(), body.GetProperty("createdAt").GetString());
        await factory.UpdateDatabaseAsync(async db => Assert.Equal(
            user.ClinicId,
            (await db.Patients.IgnoreQueryFilters().SingleAsync(p => p.Id == id)).ClinicId));
    }

    [Fact]
    public async Task Update_UnknownPatient_Is404()
    {
        var (http, _, _) = await ReceptionWithClientAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await http.PatchAsJsonAsync($"/api/patients/{Guid.NewGuid()}", new { name = "x" })).StatusCode);
    }

    // ---------- List ----------

    [Fact]
    public async Task List_SearchesPatientOwnerAndMicrochip()
    {
        var (http, _, jane) = await ReceptionWithClientAsync();
        var john = await CreateClientAsync(http, "John", "Smith");
        await CreatePatientAsync(http, new { clientId = jane, name = "Rex", species = "Dog", microchipNumber = "985112003456789" });
        await CreatePatientAsync(http, new { clientId = john, name = "Tom", species = "Cat" });
        await CreatePatientAsync(http, new { clientId = john, name = "Polly_1", species = "Bird" });

        Assert.Equal(["Rex"], await ListNamesAsync(http, "q=REX"));
        Assert.Equal(["Polly_1", "Tom"], await ListNamesAsync(http, "q=smith"));
        Assert.Equal(["Rex"], await ListNamesAsync(http, "q=jane%20doe"));
        Assert.Equal(["Rex"], await ListNamesAsync(http, "q=003-456"));
        Assert.Equal(["Polly_1"], await ListNamesAsync(http, "q=_"));
        Assert.Empty(await ListNamesAsync(http, "q=-")); // separators alone must not match every chip
    }

    [Fact]
    public async Task List_ByAnotherClinicsClient_IsEmpty()
    {
        var (httpA, _, clientA) = await ReceptionWithClientAsync();
        var (httpB, _, clientB) = await ReceptionWithClientAsync();
        await CreatePatientIdAsync(httpA, clientA);
        await CreatePatientIdAsync(httpB, clientB);

        var page = await httpA.GetFromJsonAsync<JsonElement>($"/api/patients?clientId={clientB}");

        Assert.Equal(0, page.GetProperty("pagination").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Update_ReportsFieldErrorsBeforeAMicrochipConflict()
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();
        await CreatePatientAsync(http, new { clientId, name = "Rex", species = "Dog", microchipNumber = "222" });
        var other = await CreatePatientIdAsync(http, clientId, "Max");

        var response = await http.PatchAsJsonAsync($"/api/patients/{other}", new { microchipNumber = "222", weightKg = -1 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("weightKg", Assert.Single((await ErrorOf(response)).Details).Field);
    }

    [Fact]
    public async Task List_FiltersByClientSpeciesAndStatus_AndSorts()
    {
        var (http, _, jane) = await ReceptionWithClientAsync();
        var john = await CreateClientAsync(http, "John", "Smith");
        await CreatePatientIdAsync(http, jane, "Bella", "Dog");
        var tom = await CreatePatientIdAsync(http, john, "Tom", "Cat");
        await CreatePatientIdAsync(http, john, "Ace", "Dog");
        await http.PatchAsJsonAsync($"/api/patients/{tom}", new { status = "Deceased" });

        Assert.Equal(["Ace", "Bella", "Tom"], await ListNamesAsync(http, ""));
        Assert.Equal(["Tom", "Bella", "Ace"], await ListNamesAsync(http, "sort=-name"));
        Assert.Equal(["Ace", "Tom"], await ListNamesAsync(http, $"clientId={john}"));
        Assert.Equal(["Ace", "Bella"], await ListNamesAsync(http, "species=Dog"));
        Assert.Equal(["Tom"], await ListNamesAsync(http, "status=Deceased"));
        Assert.Equal(["Ace"], await ListNamesAsync(http, $"clientId={john}&species=Dog&status=Active"));
    }

    [Theory]
    [InlineData("species=dog", "species")]
    [InlineData("status=Dead", "status")]
    [InlineData("sort=--name", "sort")]
    [InlineData("sort=dateOfBirth", "sort")]
    [InlineData("clientId=not-a-guid", "clientId")]
    public async Task List_RejectsInvalidParameters_With422(string query, string field)
    {
        var (http, _, _) = await ReceptionWithClientAsync();

        var response = await http.GetAsync("/api/patients?" + query);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, detail => detail.Field == field);
    }

    // ---------- Contract, database rules, audit ----------

    [Fact]
    public async Task PatientObject_HasExactlyTheDocumentedFields()
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();

        var body = await CreatePatientAsync(http, new { clientId, name = "Rex", species = "Dog" });

        Assert.Equal(
            [
                "id", "name", "species", "breed", "sex", "reproductiveStatus", "dateOfBirth", "dateOfBirthEstimated",
                "markings", "microchipNumber", "weightKg", "status", "dateOfDeath", "notes", "client", "createdAt",
                "updatedAt",
            ],
            body.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["id", "firstName", "lastName"], body.GetProperty("client").EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("UPDATE patients SET species = 'Dragon' WHERE id = {0}")]
    [InlineData("UPDATE patients SET weight_kg = 0 WHERE id = {0}")]
    [InlineData("UPDATE patients SET microchip_number = 'abc' WHERE id = {0}")]
    [InlineData("UPDATE patients SET name = ' ' WHERE id = {0}")]
    [InlineData("UPDATE patients SET date_of_death = '2024-01-01' WHERE id = {0}")]
    [InlineData("UPDATE patients SET status = 'Deceased', date_of_birth = '2020-01-01', date_of_death = '2019-01-01' WHERE id = {0}")]
    [InlineData("UPDATE patients SET weight_kg = 2000.001 WHERE id = {0}")]
    [InlineData("UPDATE patients SET date_of_birth = NULL, date_of_birth_estimated = true WHERE id = {0}")]
    public async Task Database_RejectsInvalidPatientValues(string sql)
    {
        var (http, _, clientId) = await ReceptionWithClientAsync();
        var id = await CreatePatientIdAsync(http, clientId);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Changes_AreAudited_WithoutNamesOrNotes()
    {
        var (http, user, clientId) = await ReceptionWithClientAsync();
        var id = await CreatePatientIdAsync(http, clientId, "Rexington");
        await http.PatchAsJsonAsync($"/api/patients/{id}", new { notes = "Secret note", weightKg = 12 });
        await http.PatchAsJsonAsync($"/api/patients/{id}", new { weightKg = 12 }); // no change, no entry

        var entries = await factory.AuditEntriesAsync(id);

        Assert.Equal([AuditActions.PatientCreated, AuditActions.PatientUpdated], entries.Select(e => e.Action));
        Assert.All(entries, entry =>
        {
            Assert.Equal(user.Id, entry.ActorUserId);
            Assert.Equal("Patient", entry.EntityType);
            Assert.DoesNotContain("Rexington", entry.Metadata ?? "");
            Assert.DoesNotContain("Secret", entry.Metadata ?? "");
        });
        Assert.Equal(clientId, JsonDocument.Parse(entries[0].Metadata!).RootElement.GetProperty("clientId").GetGuid());
    }
}
