using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>User management, permissions and tenant isolation (B-04) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class UsersTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static object NewUserBody(params Role[] roles) => new
    {
        name = "New Staff",
        email = NewEmail(),
        password = "a-long-enough-password",
        roles = (roles.Length == 0 ? [Role.Receptionist] : roles).Select(role => role.ToString()).ToArray(),
    };

    private static async Task<string> ErrorCodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error.Code;

    // ---------- Permissions ----------

    [Theory]
    [InlineData(Role.Veterinarian)]
    [InlineData(Role.Nurse)]
    [InlineData(Role.Receptionist)]
    [InlineData(Role.Inventory)]
    public async Task NonAdministrators_CannotReadOrManageUsers(Role role)
    {
        var (client, user) = await factory.SignedInAsync(role);

        var responses = new[]
        {
            await client.GetAsync("/api/users"),
            await client.GetAsync($"/api/users/{user.Id}"),
            await client.PostAsJsonAsync("/api/users", NewUserBody()),
            await client.PatchAsJsonAsync($"/api/users/{user.Id}", new { name = "x" }),
            await client.PostAsJsonAsync($"/api/users/{user.Id}/roles", new { roles = new[] { "Admin" } }),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(ErrorCodes.Forbidden, await ErrorCodeOf(response));
        }
    }

    [Fact]
    public async Task UserWithNoRoles_CanStillSeeThemselves_ButNothingElse()
    {
        var (client, _) = await factory.SignedInAsync();

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");

        Assert.Empty(me.GetProperty("roles").EnumerateArray());
        Assert.Empty(me.GetProperty("permissions").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Me_ReturnsRolesAndTheirPermissions()
    {
        var (client, _) = await factory.SignedInAsync(Role.Admin, Role.Veterinarian);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");

        Assert.Equal(["Admin", "Veterinarian"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(
            [Permissions.UsersManage, Permissions.UsersRead],
            me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task RoleChanges_ApplyToTheVeryNextRequest()
    {
        var (adminClient, admin) = await factory.SignedInAsync(Role.Admin);
        var colleague = await factory.CreateColleagueAsync(admin, roles: Role.Admin);
        var colleagueClient = await factory.SignInAsync(colleague);
        Assert.Equal(HttpStatusCode.OK, (await colleagueClient.GetAsync("/api/users")).StatusCode);

        var demote = await adminClient.PostAsJsonAsync($"/api/users/{colleague.Id}/roles", new { roles = new[] { "Nurse" } });

        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleagueClient.GetAsync("/api/users")).StatusCode);
    }

    // ---------- Tenant isolation ----------

    [Fact]
    public async Task Administrators_OnlyEverSeeTheirOwnClinicsUsers()
    {
        var (clientA, adminA) = await factory.SignedInAsync(Role.Admin);
        var (_, adminB) = await factory.SignedInAsync(Role.Admin);
        var colleagueA = await factory.CreateColleagueAsync(adminA);

        var page = await clientA.GetFromJsonAsync<JsonElement>("/api/users?pageSize=100");
        var ids = page.GetProperty("data").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([adminA.Id, colleagueA.Id], ids.Order());
        Assert.Equal(2, page.GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.DoesNotContain(adminB.Id, ids);
    }

    [Fact]
    public async Task OtherClinicsUsers_AreNotFound_ForEveryOperation()
    {
        var (clientA, _) = await factory.SignedInAsync(Role.Admin);
        var (_, adminB) = await factory.SignedInAsync(Role.Admin);

        var responses = new[]
        {
            await clientA.GetAsync($"/api/users/{adminB.Id}"),
            await clientA.PatchAsJsonAsync($"/api/users/{adminB.Id}", new { status = "Disabled" }),
            await clientA.PostAsJsonAsync($"/api/users/{adminB.Id}/roles", new { roles = new[] { "Nurse" } }),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ErrorCodes.NotFound, await ErrorCodeOf(response));
        }

        var untouched = await factory.SignInAsync(adminB); // still active, still an administrator
        Assert.Equal(HttpStatusCode.OK, (await untouched.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task CreatedUsers_AlwaysBelongToTheCreatorsClinic()
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);

        var created = await client.PostAsJsonAsync("/api/users", NewUserBody());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await factory.UpdateDatabaseAsync(async db =>
            Assert.Equal(admin.ClinicId, (await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == id)).ClinicId));
    }

    [Fact]
    public async Task WithoutACurrentClinic_ClinicOwnedQueriesReturnNothing()
    {
        await factory.CreateUserAsync(NewEmail(), DatabaseApiFactory.TestPassword);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await db.Users.ToListAsync()); // fail closed
        Assert.NotEmpty(await db.Users.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task SavingAnotherClinicsRow_FromWithinAClinicRequest_Throws()
    {
        // Simulates a bug that loads a row across clinics and then edits it: the write-side guard must stop it.
        var (_, adminA) = await factory.SignedInAsync(Role.Admin);
        var (_, adminB) = await factory.SignedInAsync(Role.Admin);
        await using var scope = factory.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
        accessor.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(Api.Features.Auth.AuthClaims.ClinicId, adminA.ClinicId.ToString())],
                "Test")),
        };
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var other = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == adminB.Id);
        other.Name = "Hijacked";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        accessor.HttpContext = null;
    }

    // ---------- Create / read / update ----------

    [Fact]
    public async Task Create_Returns201WithLocation_AndTheNewUserCanSignIn()
    {
        var (client, _) = await factory.SignedInAsync(Role.Admin);
        var email = NewEmail();

        var response = await client.PostAsJsonAsync("/api/users", new
        {
            name = "  Sam Nurse ", email = "  " + email.ToUpperInvariant(), phone = "+1 555 0100",
            password = "a-long-enough-password", roles = new[] { "Nurse", "Nurse" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"/api/users/{body.GetProperty("id").GetGuid()}", response.Headers.Location!.AbsolutePath);
        Assert.Equal("Sam Nurse", body.GetProperty("name").GetString());
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.Equal(["Nurse"], body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.False(body.TryGetProperty("passwordHash", out _));

        var newClient = await factory.CreateClientWithCsrfAsync();
        var login = await newClient.PostAsJsonAsync("/api/auth/login", new { email, password = "a-long-enough-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Theory]
    [InlineData("""{"name":"A","email":"a@x.com","password":"short","roles":["Nurse"]}""", "password")]
    [InlineData("""{"name":"A","email":"not-an-email","password":"a-long-enough-password","roles":["Nurse"]}""", "email")]
    [InlineData("""{"name":"A","email":"a@x.com","password":"a-long-enough-password","roles":[]}""", "roles")]
    [InlineData("""{"name":"A","email":"a@x.com","password":"a-long-enough-password","roles":["Superuser"]}""", null)]
    [InlineData("""{"email":"a@x.com","password":"a-long-enough-password","roles":["Nurse"]}""", "name")]
    public async Task Create_RejectsInvalidInput(string json, string? field)
    {
        var (client, _) = await factory.SignedInAsync(Role.Admin);

        var response = await client.PostAsync("/api/users", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        // An unknown role name cannot be parsed, so it is a malformed body (400); everything else is a 422.
        Assert.Equal(field is null ? HttpStatusCode.BadRequest : HttpStatusCode.UnprocessableEntity, response.StatusCode);
        if (field is not null)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            Assert.Contains(error!.Error.Details, detail => detail.Field == field);
        }
    }

    [Fact]
    public async Task Create_WithAnEmailAlreadyUsedInAnyClinic_Returns409()
    {
        var (client, _) = await factory.SignedInAsync(Role.Admin);
        var (_, elsewhere) = await factory.SignedInAsync();

        var response = await client.PostAsJsonAsync("/api/users", new
        {
            name = "Dup", email = elsewhere.Email.ToUpperInvariant(), password = "a-long-enough-password", roles = new[] { "Nurse" },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, await ErrorCodeOf(response));
    }

    [Fact]
    public async Task List_SupportsSearchFilterSortAndPagination()
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);
        foreach (var (name, role, status) in new[]
                 {
                     ("Alice Vet", Role.Veterinarian, UserStatus.Active),
                     ("Bob Vet", Role.Veterinarian, UserStatus.Disabled),
                     ("Carol Nurse", Role.Nurse, UserStatus.Active),
                     ("100%_literal", Role.Nurse, UserStatus.Active),
                 })
        {
            var user = await factory.CreateColleagueAsync(admin, status, role);
            await factory.UpdateDatabaseAsync(db => db.Users.IgnoreQueryFilters().Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(u => u.Name, name)));
        }

        async Task<List<string>> Names(string query) =>
            (await client.GetFromJsonAsync<JsonElement>("/api/users?" + query)).GetProperty("data").EnumerateArray()
            .Select(u => u.GetProperty("name").GetString()!).ToList();

        Assert.Equal(["Alice Vet", "Bob Vet"], await Names("q=vet"));
        Assert.Equal(["Alice Vet"], await Names("q=vet&status=Active"));
        Assert.Equal(["100%_literal", "Carol Nurse"], await Names("role=Nurse"));
        Assert.Equal(["100%_literal"], await Names("q=%25_"));             // wildcards are matched literally
        Assert.Equal(["Bob Vet", "Alice Vet"], await Names("role=Veterinarian&sort=-name"));

        var page2 = await client.GetFromJsonAsync<JsonElement>("/api/users?pageSize=2&page=2");
        Assert.Equal(2, page2.GetProperty("data").GetArrayLength());
        Assert.Equal(5, page2.GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.Equal(3, page2.GetProperty("pagination").GetProperty("totalPages").GetInt32());
    }

    [Theory]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("page=0", "page")]
    [InlineData("sort=password", "sort")]
    [InlineData("status=Sleeping", "status")]
    public async Task List_RejectsInvalidQueryParameters(string query, string field)
    {
        var (client, _) = await factory.SignedInAsync(Role.Admin);

        var response = await client.GetAsync("/api/users?" + query);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Contains(error!.Error.Details, detail => detail.Field == field);
    }

    [Fact]
    public async Task Update_ChangesOnlyTheGivenFields_AndEmptyPhoneClearsIt()
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);
        var colleague = await factory.CreateColleagueAsync(admin, roles: Role.Nurse);
        await client.PatchAsJsonAsync($"/api/users/{colleague.Id}", new { phone = "+1 555 0100" });

        var response = await client.PatchAsJsonAsync($"/api/users/{colleague.Id}", new { name = "Renamed", phone = "" });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed", body.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("phone").ValueKind);
        Assert.Equal(colleague.Email, body.GetProperty("email").GetString());
        Assert.Equal(["Nurse"], body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task DisablingAUser_SignsThemOutAndBlocksSignIn()
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);
        var colleague = await factory.CreateColleagueAsync(admin, roles: Role.Nurse);
        var colleagueClient = await factory.SignInAsync(colleague);

        var response = await client.PatchAsJsonAsync($"/api/users/{colleague.Id}", new { status = "Disabled" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await colleagueClient.GetAsync("/api/auth/me")).StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.SignInAsync(colleague));
    }

    // ---------- Administrator safety ----------

    [Fact]
    public async Task AnAdministrator_CannotDisableThemselves()
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);
        await factory.CreateColleagueAsync(admin, roles: Role.Admin);

        var response = await client.PatchAsJsonAsync($"/api/users/{admin.Id}", new { status = "Disabled" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TheLastActiveAdministrator_CannotBeDemoted_OrDisabled()
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);
        var disabledAdmin = await factory.CreateColleagueAsync(admin, UserStatus.Disabled, Role.Admin);

        var demoteSelf = await client.PostAsJsonAsync($"/api/users/{admin.Id}/roles", new { roles = new[] { "Veterinarian" } });
        Assert.Equal(HttpStatusCode.Conflict, demoteSelf.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, await ErrorCodeOf(demoteSelf));

        // A second, active administrator makes both changes allowed.
        var other = await factory.CreateColleagueAsync(admin, roles: Role.Admin);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/users/{other.Id}", new { status = "Disabled" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/users/{admin.Id}/roles", new { roles = new[] { "Nurse" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/users/{disabledAdmin.Id}", new { status = "Active" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/users/{admin.Id}/roles", new { roles = new[] { "Nurse" } })).StatusCode);
    }

    [Fact]
    public async Task TwoAdministratorsDemotingEachOtherAtOnce_LeaveOneAdministrator()
    {
        var (clientA, adminA) = await factory.SignedInAsync(Role.Admin);
        var adminB = await factory.CreateColleagueAsync(adminA, roles: Role.Admin);
        var clientB = await factory.SignInAsync(adminB);

        var results = await Task.WhenAll(
            clientA.PostAsJsonAsync($"/api/users/{adminB.Id}/roles", new { roles = new[] { "Nurse" } }),
            clientB.PostAsJsonAsync($"/api/users/{adminA.Id}/roles", new { roles = new[] { "Nurse" } }));

        // Exactly one demotion wins. The other is refused: 409 if it checked the admin count after the winner
        // committed, or 403 if its own admin role was already gone when it was authorised.
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, result => result.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden);
        await factory.UpdateDatabaseAsync(async db => Assert.Equal(1, await db.Users.IgnoreQueryFilters().CountAsync(u =>
            u.ClinicId == adminA.ClinicId && u.Roles.Any(role => role.Role == Role.Admin))));
    }

    // ---------- Audit ----------

    [Fact]
    public async Task UserChanges_AreAudited_WithActorClinicAndRequestId()
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);

        var created = await client.PostAsJsonAsync("/api/users", NewUserBody(Role.Nurse));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await client.PatchAsJsonAsync($"/api/users/{id}", new { name = "Renamed" });
        await client.PostAsJsonAsync($"/api/users/{id}/roles", new { roles = new[] { "Veterinarian" } });

        var entries = await factory.AuditEntriesAsync(id);
        Assert.Equal(
            [AuditActions.UserCreated, AuditActions.UserUpdated, AuditActions.UserRolesChanged],
            entries.Select(entry => entry.Action));
        Assert.All(entries, entry =>
        {
            Assert.Equal(admin.Id, entry.ActorUserId);
            Assert.Equal(admin.ClinicId, entry.ClinicId);
            Assert.False(string.IsNullOrEmpty(entry.RequestId));
            Assert.DoesNotContain("password", entry.Metadata ?? "", StringComparison.OrdinalIgnoreCase);
        });
        var rolesChange = JsonDocument.Parse(entries[2].Metadata!).RootElement;
        Assert.Equal(["Nurse"], rolesChange.GetProperty("before").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(["Veterinarian"], rolesChange.GetProperty("after").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task AuthenticationEvents_AreAudited()
    {
        var (client, user) = await factory.SignedInAsync(Role.Nurse);
        await client.PostAsync("/api/auth/logout", content: null);
        var other = await factory.CreateClientWithCsrfAsync();
        await other.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "wrong-password-1" });

        var actions = (await factory.AuditEntriesAsync(user.Id)).Select(entry => entry.Action).ToList();

        Assert.Equal([AuditActions.LoginSucceeded, AuditActions.LoginFailed], actions);
        await factory.UpdateDatabaseAsync(async db => Assert.True(await db.AuditLogs.AnyAsync(entry =>
            entry.Action == AuditActions.Logout && entry.ActorUserId == user.Id)));
    }

    [Fact]
    public async Task Lockout_IsAudited_ExactlyOnce()
    {
        var user = await factory.CreateUserAsync(NewEmail(), DatabaseApiFactory.TestPassword);
        var clients = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => factory.CreateClientWithCsrfAsync()));

        await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "wrong-password-1" })));

        var entries = await factory.AuditEntriesAsync(user.Id);
        Assert.Single(entries, entry => entry.Action == AuditActions.AccountLocked);
        Assert.All(entries, entry => Assert.DoesNotContain(user.Email, entry.Metadata ?? ""));
    }

    [Fact]
    public async Task AuditLog_IsAppendOnly_InTheDatabase()
    {
        var (_, user) = await factory.SignedInAsync();

        foreach (var sql in new[]
                 {
                     "UPDATE audit_logs SET action = 'tampered' WHERE entity_id = {0}",
                     "DELETE FROM audit_logs WHERE entity_id = {0}",
                     "TRUNCATE audit_logs",
                 })
        {
            var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, user.Id)));
            Assert.Contains("append-only", exception.MessageText);
        }
    }
}
