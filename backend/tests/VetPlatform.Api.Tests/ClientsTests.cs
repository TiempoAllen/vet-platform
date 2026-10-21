using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Client records, their permissions and tenant isolation (B-05) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class ClientsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static object NewClientBody(string firstName = "Jane", string lastName = "Doe") =>
        new { firstName, lastName };

    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private static async Task<JsonElement> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/clients", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<string>> ListNamesAsync(HttpClient client, string query)
    {
        var page = await client.GetFromJsonAsync<JsonElement>("/api/clients?" + query);
        return page.GetProperty("data").EnumerateArray()
            .Select(c => c.GetProperty("firstName").GetString() + " " + c.GetProperty("lastName").GetString())
            .ToList();
    }

    // ---------- Permissions ----------

    [Theory]
    [InlineData(Role.Inventory)]
    [InlineData(null)]
    public async Task UsersWithoutClientAccess_CannotReadOrManageClients(Role? role)
    {
        var (client, _) = await factory.SignedInAsync(role is { } r ? [r] : []);
        var id = Guid.NewGuid();

        var responses = new[]
        {
            await client.GetAsync("/api/clients"),
            await client.GetAsync($"/api/clients/{id}"),
            await client.PostAsJsonAsync("/api/clients", NewClientBody()),
            await client.PatchAsJsonAsync($"/api/clients/{id}", new { firstName = "x" }),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(ErrorCodes.Forbidden, (await ErrorOf(response)).Code);
        }
    }

    [Theory]
    [InlineData(Role.Veterinarian)]
    [InlineData(Role.Nurse)]
    public async Task ClinicalStaff_CanReadClients_ButNotChangeThem(Role role)
    {
        var (receptionist, staffMember) = await factory.SignedInAsync(Role.Receptionist);
        var id = (await CreateAsync(receptionist, NewClientBody())).GetProperty("id").GetGuid();
        var colleague = await factory.CreateColleagueAsync(staffMember, roles: role);
        var client = await factory.SignInAsync(colleague);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/clients")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clients/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/clients", NewClientBody())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PatchAsJsonAsync($"/api/clients/{id}", new { firstName = "x" })).StatusCode);
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.Receptionist)]
    public async Task AdministratorsAndReceptionists_CanManageClients(Role role)
    {
        var (client, _) = await factory.SignedInAsync(role);

        var id = (await CreateAsync(client, NewClientBody())).GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PatchAsJsonAsync($"/api/clients/{id}", new { status = "Inactive" })).StatusCode);
    }

    [Fact]
    public async Task Me_ListsClientPermissions()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");

        Assert.Equal(
            [Permissions.ClientsManage, Permissions.ClientsRead],
            me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    // ---------- Tenant isolation ----------

    [Fact]
    public async Task Staff_OnlyEverSeeTheirOwnClinicsClients()
    {
        var (clientA, _) = await factory.SignedInAsync(Role.Receptionist);
        var (clientB, _) = await factory.SignedInAsync(Role.Receptionist);
        var mine = (await CreateAsync(clientA, NewClientBody("Mine", "Client"))).GetProperty("id").GetGuid();
        await CreateAsync(clientB, NewClientBody("Their", "Client"));

        var page = await clientA.GetFromJsonAsync<JsonElement>("/api/clients?q=client");

        Assert.Equal([mine], page.GetProperty("data").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
        Assert.Equal(1, page.GetProperty("pagination").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task OtherClinicsClients_AreNotFound_AndUnchanged()
    {
        var (clientA, _) = await factory.SignedInAsync(Role.Admin);
        var (clientB, _) = await factory.SignedInAsync(Role.Admin);
        var theirs = (await CreateAsync(clientB, NewClientBody())).GetProperty("id").GetGuid();

        var responses = new[]
        {
            await clientA.GetAsync($"/api/clients/{theirs}"),
            await clientA.PatchAsJsonAsync($"/api/clients/{theirs}", new { firstName = "Hijacked", status = "Inactive" }),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ErrorCodes.NotFound, (await ErrorOf(response)).Code);
        }

        var untouched = await clientB.GetFromJsonAsync<JsonElement>($"/api/clients/{theirs}");
        Assert.Equal("Jane", untouched.GetProperty("firstName").GetString());
        Assert.Equal("Active", untouched.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreatedClients_BelongToTheCreatorsClinic_WhateverTheBodySays()
    {
        var (client, user) = await factory.SignedInAsync(Role.Receptionist);
        var (_, other) = await factory.SignedInAsync(Role.Receptionist);

        var id = (await CreateAsync(client, new { firstName = "Jane", lastName = "Doe", clinicId = other.ClinicId }))
            .GetProperty("id").GetGuid();

        await factory.UpdateDatabaseAsync(async db =>
            Assert.Equal(user.ClinicId, (await db.Clients.IgnoreQueryFilters().SingleAsync(c => c.Id == id)).ClinicId));
    }

    // ---------- Create / read ----------

    [Fact]
    public async Task Create_Returns201WithLocation_AndNormalisesInput()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);

        var response = await client.PostAsJsonAsync("/api/clients", new
        {
            firstName = "  Jane ", lastName = " Doe ", email = "  Jane.Doe@Example.COM ", phone = " +1 555 0100 ",
            addressLine1 = "1 Main St", addressLine2 = "   ", city = "Springfield", region = "IL", postalCode = "62701",
            country = "US", preferredContactMethod = "Sms", notes = "Prefers mornings.",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/api/clients/{id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal("Jane", body.GetProperty("firstName").GetString());
        Assert.Equal("Doe", body.GetProperty("lastName").GetString());
        Assert.Equal("jane.doe@example.com", body.GetProperty("email").GetString());
        Assert.Equal("+1 555 0100", body.GetProperty("phone").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("addressLine2").ValueKind);
        Assert.Equal("Sms", body.GetProperty("preferredContactMethod").GetString());
        Assert.Equal("Active", body.GetProperty("status").GetString());

        var fetched = await client.GetFromJsonAsync<JsonElement>($"/api/clients/{id}");
        Assert.Equal(body.ToString(), fetched.ToString());
    }

    [Fact]
    public async Task Create_WithOnlyNames_LeavesEverythingElseEmpty()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);

        var body = await CreateAsync(client, new { firstName = "Jane", lastName = "Doe", email = "" });

        foreach (var field in new[] { "email", "phone", "addressLine1", "city", "preferredContactMethod", "notes" })
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty(field).ValueKind);
        }
    }

    [Theory]
    [InlineData("""{ "lastName": "Doe" }""", "firstName")]
    [InlineData("""{ "firstName": "  ", "lastName": "Doe" }""", "firstName")]
    [InlineData("""{ "firstName": "Jane" }""", "lastName")]
    [InlineData("""{ "firstName": "Jane", "lastName": "Doe", "email": "not-an-email" }""", "email")]
    [InlineData("""{ "firstName": "Jane", "lastName": "Doe", "postalCode": "123456789012345678901" }""", "postalCode")]
    public async Task Create_RejectsInvalidFields_With422(string json, string field)
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);

        var response = await client.PostAsync(
            "/api/clients", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal(ErrorCodes.ValidationError, error.Code);
        Assert.Contains(error.Details, detail => detail.Field == field);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("EMAIL")]
    [InlineData("Post")]
    public async Task Create_RejectsUnknownContactMethod_With400(string method)
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);

        var response = await client.PostAsJsonAsync(
            "/api/clients", new { firstName = "Jane", lastName = "Doe", preferredContactMethod = method });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownClient_Is404()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);

        var response = await client.GetAsync($"/api/clients/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Clients_CannotBeDeleted()
    {
        var (client, _) = await factory.SignedInAsync(Role.Admin);
        var id = (await CreateAsync(client, NewClientBody())).GetProperty("id").GetGuid();

        var response = await client.DeleteAsync($"/api/clients/{id}");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clients/{id}")).StatusCode);
    }

    // ---------- Update ----------

    [Fact]
    public async Task Update_ChangesOnlyTheGivenFields_AndBlankClearsOptionalOnes()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);
        var id = (await CreateAsync(client, new
        {
            firstName = "Jane", lastName = "Doe", email = "jane@example.com", phone = "555 0100", city = "Springfield",
            preferredContactMethod = "Email",
        })).GetProperty("id").GetGuid();

        var response = await client.PatchAsJsonAsync($"/api/clients/{id}", new
        {
            lastName = " Smith ", phone = "", email = (string?)null, city = "Shelbyville",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Jane", body.GetProperty("firstName").GetString());
        Assert.Equal("Smith", body.GetProperty("lastName").GetString());
        Assert.Equal("jane@example.com", body.GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("phone").ValueKind);
        Assert.Equal("Shelbyville", body.GetProperty("city").GetString());
        Assert.Equal("Email", body.GetProperty("preferredContactMethod").GetString()); // omitted: unchanged
    }

    [Fact]
    public async Task Update_ExplicitNullContactMethod_ClearsIt()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);
        var id = (await CreateAsync(client, new { firstName = "Jane", lastName = "Doe", preferredContactMethod = "Phone" }))
            .GetProperty("id").GetGuid();

        var changed = await client.PatchAsJsonAsync($"/api/clients/{id}", new { preferredContactMethod = "Sms" });
        Assert.Equal("Sms", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("preferredContactMethod").GetString());

        var cleared = await client.PatchAsync(
            $"/api/clients/{id}",
            new StringContent("""{ "preferredContactMethod": null }""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(
            JsonValueKind.Null,
            (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("preferredContactMethod").ValueKind);
    }

    [Fact]
    public async Task Update_CanDeactivateAndReactivate()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);
        var id = (await CreateAsync(client, NewClientBody())).GetProperty("id").GetGuid();

        var deactivated = await client.PatchAsJsonAsync($"/api/clients/{id}", new { status = "Inactive" });
        Assert.Equal("Inactive", (await deactivated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var reactivated = await client.PatchAsJsonAsync($"/api/clients/{id}", new { status = "Active" });
        Assert.Equal("Active", (await reactivated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("""{ "firstName": " " }""", "firstName")]
    [InlineData("""{ "lastName": "" }""", "lastName")]
    [InlineData("""{ "email": "nope" }""", "email")]
    public async Task Update_RejectsInvalidFields_With422(string json, string field)
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);
        var id = (await CreateAsync(client, NewClientBody())).GetProperty("id").GetGuid();

        var response = await client.PatchAsync(
            $"/api/clients/{id}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, detail => detail.Field == field);
    }

    [Fact]
    public async Task Update_RejectsUnknownStatus_With400()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);
        var id = (await CreateAsync(client, NewClientBody())).GetProperty("id").GetGuid();

        var response = await client.PatchAsJsonAsync($"/api/clients/{id}", new { status = "Deleted" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_UnknownClient_Is404()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);

        var response = await client.PatchAsJsonAsync($"/api/clients/{Guid.NewGuid()}", new { firstName = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- List ----------

    [Fact]
    public async Task List_SearchesNamesEmailAndPhone_CaseInsensitively()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);
        await CreateAsync(client, new { firstName = "Jane", lastName = "Doe", email = "jd@example.com", phone = "555 0100" });
        await CreateAsync(client, new { firstName = "John", lastName = "Smith", email = "smithy@example.org" });
        await CreateAsync(client, new { firstName = "Ann", lastName = "Lee_Jones" });

        Assert.Equal(["Jane Doe"], await ListNamesAsync(client, "q=JANE"));
        Assert.Equal(["Jane Doe"], await ListNamesAsync(client, "q=jane%20doe"));
        Assert.Equal(["John Smith"], await ListNamesAsync(client, "q=example.org"));
        Assert.Equal(["Jane Doe"], await ListNamesAsync(client, "q=0100"));
        Assert.Equal(["Ann Lee_Jones"], await ListNamesAsync(client, "q=_")); // wildcards match literally
        Assert.Empty(await ListNamesAsync(client, "q=%25"));
    }

    [Fact]
    public async Task List_FiltersByStatus_AndSortsByName()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);
        await CreateAsync(client, NewClientBody("Zoe", "Adams"));
        await CreateAsync(client, NewClientBody("Amy", "Brown"));
        var inactive = (await CreateAsync(client, NewClientBody("Bob", "Adams"))).GetProperty("id").GetGuid();
        await client.PatchAsJsonAsync($"/api/clients/{inactive}", new { status = "Inactive" });

        Assert.Equal(["Bob Adams", "Zoe Adams", "Amy Brown"], await ListNamesAsync(client, ""));
        Assert.Equal(["Amy Brown", "Zoe Adams", "Bob Adams"], await ListNamesAsync(client, "sort=-name"));
        Assert.Equal(["Zoe Adams", "Amy Brown"], await ListNamesAsync(client, "status=Active"));
        Assert.Equal(["Bob Adams"], await ListNamesAsync(client, "status=Inactive"));
    }

    [Fact]
    public async Task List_SortsByCreatedAt_AndPages()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);
        foreach (var name in new[] { "C", "A", "B" })
        {
            await CreateAsync(client, NewClientBody(name, "Client"));
            factory.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(["C Client", "A Client", "B Client"], await ListNamesAsync(client, "sort=createdAt"));
        Assert.Equal(["B Client", "A Client", "C Client"], await ListNamesAsync(client, "sort=-createdAt"));

        var page = await client.GetFromJsonAsync<JsonElement>("/api/clients?sort=createdAt&page=2&pageSize=2");
        Assert.Equal("B", page.GetProperty("data")[0].GetProperty("firstName").GetString());
        Assert.Equal(3, page.GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("pagination").GetProperty("totalPages").GetInt32());
    }

    [Theory]
    [InlineData("sort=email", "sort")]
    [InlineData("status=active", "status")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("page=0", "page")]
    public async Task List_RejectsInvalidParameters_With422(string query, string field)
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);

        var response = await client.GetAsync("/api/clients?" + query);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await ErrorOf(response)).Details, detail => detail.Field == field);
    }

    // ---------- Contract and database rules ----------

    [Fact]
    public async Task ClientObject_HasExactlyTheDocumentedFields()
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);

        var body = await CreateAsync(client, NewClientBody());

        Assert.Equal(
            [
                "id", "firstName", "lastName", "email", "phone", "addressLine1", "addressLine2", "city", "region",
                "postalCode", "country", "preferredContactMethod", "notes", "status", "createdAt", "updatedAt",
            ],
            body.EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData("UPDATE clients SET status = 'Deleted' WHERE id = {0}")]
    [InlineData("UPDATE clients SET preferred_contact_method = 'Fax' WHERE id = {0}")]
    public async Task Database_RejectsInvalidClientValues(string sql)
    {
        var (client, _) = await factory.SignedInAsync(Role.Receptionist);
        var id = (await CreateAsync(client, NewClientBody())).GetProperty("id").GetGuid();

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync(sql, id)));

        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    // ---------- Audit ----------

    [Fact]
    public async Task Changes_AreAudited_WithoutPersonalData()
    {
        var (client, user) = await factory.SignedInAsync(Role.Receptionist);
        var id = (await CreateAsync(client, new { firstName = "Jane", lastName = "Doe", email = "jane@example.com" }))
            .GetProperty("id").GetGuid();
        await client.PatchAsJsonAsync($"/api/clients/{id}", new { email = "jane.doe@example.com", status = "Inactive" });
        await client.PatchAsJsonAsync($"/api/clients/{id}", new { status = "Inactive" }); // no change, no entry

        var entries = await factory.AuditEntriesAsync(id);

        Assert.Equal([AuditActions.ClientCreated, AuditActions.ClientUpdated], entries.Select(e => e.Action));
        Assert.All(entries, entry =>
        {
            Assert.Equal(user.Id, entry.ActorUserId);
            Assert.Equal(user.ClinicId, entry.ClinicId);
            Assert.Equal("Client", entry.EntityType);
            Assert.DoesNotContain("jane", entry.Metadata ?? "", StringComparison.OrdinalIgnoreCase);
        });
        var metadata = JsonDocument.Parse(entries[1].Metadata!).RootElement;
        Assert.Equal(["email", "status"], metadata.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
    }
}
