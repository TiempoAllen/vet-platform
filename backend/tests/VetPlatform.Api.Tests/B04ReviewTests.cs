using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Users;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Regression tests for the B-04 security, API-contract and data-model review findings.</summary>
[Trait("Category", "Database")]
public sealed class B04ReviewTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    // ---------- Strict enums ----------

    [Theory]
    [InlineData("""{"roles":[0]}""")]
    [InlineData("""{"roles":[99]}""")]
    [InlineData("""{"roles":["0"]}""")]
    [InlineData("""{"roles":["admin"]}""")]
    [InlineData("""{"roles":["Admin, Nurse"]}""")]
    public async Task SetRoles_AcceptsOnlyExactRoleNames(string body)
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);
        var colleague = await factory.CreateColleagueAsync(admin, roles: Role.Nurse);

        var response = await client.PostAsync($"/api/users/{colleague.Id}/roles", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.BadRequest, (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error.Code);
    }

    [Theory]
    [InlineData("""{"status":7}""")]
    [InlineData("""{"status":"disabled"}""")]
    public async Task Patch_AcceptsOnlyExactStatusNames(string body)
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);
        var colleague = await factory.CreateColleagueAsync(admin, roles: Role.Nurse);

        var response = await client.PatchAsync($"/api/users/{colleague.Id}", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("role=0", "role")]
    [InlineData("role=admin", "role")]
    [InlineData("status=1", "status")]
    public async Task ListFilters_AcceptOnlyExactNames(string query, string field)
    {
        var (client, _) = await factory.SignedInAsync(Role.Admin);

        var response = await client.GetAsync("/api/users?" + query);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Contains(error!.Error.Details, detail => detail.Field == field);
    }

    // ---------- Contract details ----------

    [Fact]
    public async Task UserObject_HasExactlyTheDocumentedFields()
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);

        var user = await client.GetFromJsonAsync<JsonElement>($"/api/users/{admin.Id}");

        Assert.Equal(
            ["id", "name", "email", "phone", "status", "roles", "lastLoginAt", "createdAt", "updatedAt"],
            user.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Patch_ReturnsNotFoundForUnknownUsers_AndValidatesFields()
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/users/{Guid.NewGuid()}", new { name = "x" })).StatusCode);
        var blank = await client.PatchAsJsonAsync($"/api/users/{admin.Id}", new { name = "  " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Contains((await blank.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error.Details, d => d.Field == "name");
    }

    [Fact]
    public async Task SetRoles_RequiresAtLeastOneRole()
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);
        var colleague = await factory.CreateColleagueAsync(admin, roles: Role.Nurse);

        var response = await client.PostAsJsonAsync($"/api/users/{colleague.Id}/roles", new { roles = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---------- Signing in while already signed in ----------

    [Fact]
    public async Task SigningInToAnotherClinicsAccount_FromASignedInBrowser_Works_AndRevokesThePreviousSession()
    {
        var (browser, userA) = await factory.SignedInAsync(Role.Nurse);
        var (_, userB) = await factory.SignedInAsync(Role.Admin);

        var response = await browser.PostAsJsonAsync("/api/auth/login", new { email = userB.Email, password = DatabaseApiFactory.TestPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        browser.UseCsrfTokenFrom(response);
        var me = await browser.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(userB.Id, me.GetProperty("id").GetGuid());

        await factory.UpdateDatabaseAsync(async db =>
        {
            Assert.False(await db.UserSessions.AnyAsync(s => s.UserId == userA.Id && s.RevokedAt == null));
            var success = await db.AuditLogs.Where(e => e.EntityId == userB.Id && e.Action == AuditActions.LoginSucceeded)
                .OrderByDescending(e => e.OccurredAt).FirstAsync();
            Assert.Equal(userB.Id, success.ActorUserId);
            Assert.Equal(userB.ClinicId, success.ClinicId);
        });
    }

    [Fact]
    public async Task FailedSignInFromASignedInBrowser_IsNotAttributedToTheSignedInUser()
    {
        var (browser, userA) = await factory.SignedInAsync(Role.Nurse);
        var (_, userB) = await factory.SignedInAsync(Role.Nurse);

        await browser.PostAsJsonAsync("/api/auth/login", new { email = userB.Email, password = "wrong-password-1" });
        await browser.PostAsJsonAsync("/api/auth/login", new { email = NewEmail(), password = "wrong-password-1" });

        var failure = (await factory.AuditEntriesAsync(userB.Id)).Single(e => e.Action == AuditActions.LoginFailed);
        Assert.Null(failure.ActorUserId);
        Assert.Equal(userB.ClinicId, failure.ClinicId);
        await factory.UpdateDatabaseAsync(async db => Assert.False(await db.AuditLogs.AnyAsync(e =>
            e.Action == AuditActions.LoginFailed && (e.ActorUserId == userA.Id || (e.EntityId == null && e.ClinicId == userA.ClinicId)))));
    }

    // ---------- Password change abuse ----------

    [Fact]
    public async Task WrongCurrentPasswords_CountTowardsLockout_AndTheLockSignsEverySessionOut()
    {
        var (client, user) = await factory.SignedInAsync();
        var otherBrowser = await factory.SignInAsync(user);
        var body = new { currentPassword = "wrong-password-1", newPassword = "a-brand-new-password" };

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/auth/password", body)).StatusCode);
        }

        var locking = await client.PostAsJsonAsync("/api/auth/password", body);

        Assert.Equal(HttpStatusCode.Unauthorized, locking.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherBrowser.GetAsync("/api/auth/me")).StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.SignInAsync(user)); // locked

        var actions = (await factory.AuditEntriesAsync(user.Id)).Select(e => e.Action).ToList();
        Assert.Equal(5, actions.Count(a => a == AuditActions.PasswordChangeFailed));
        Assert.Contains(AuditActions.AccountLocked, actions);
        Assert.Contains(AuditActions.SessionsRevoked, actions);
    }

    // ---------- Disable / re-enable ----------

    [Fact]
    public async Task ReEnablingAUser_DoesNotReviveTheirOldSessions()
    {
        var (client, admin) = await factory.SignedInAsync(Role.Admin);
        var colleague = await factory.CreateColleagueAsync(admin, roles: Role.Nurse);
        var colleagueClient = await factory.SignInAsync(colleague);

        await client.PatchAsJsonAsync($"/api/users/{colleague.Id}", new { status = "Disabled" });
        await client.PatchAsJsonAsync($"/api/users/{colleague.Id}", new { status = "Active" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await colleagueClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.NotNull(await factory.SignInAsync(colleague)); // signing in again works
    }

    // ---------- Audit log integrity ----------

    [Fact]
    public async Task AuditRecordedAt_IsAlwaysTheDatabaseClock()
    {
        var (_, user) = await factory.SignedInAsync();
        var forged = Guid.CreateVersion7();

        await factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO audit_logs (id, occurred_at, recorded_at, action, actor_user_id, clinic_id) " +
            "VALUES ({0}, now(), '2000-01-01T00:00:00Z', 'test.backdated', {1}, {2})",
            forged, user.Id, user.ClinicId));

        await factory.UpdateDatabaseAsync(async db =>
        {
            var entry = await db.AuditLogs.SingleAsync(e => e.Id == forged);
            Assert.True(entry.RecordedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        });
    }

    [Fact]
    public async Task AuditEntries_CannotReferenceUnknownClinicsOrUsers()
    {
        await factory.MigrateAsync();

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync(
                "INSERT INTO audit_logs (id, occurred_at, action, actor_user_id) VALUES ({0}, now(), 'test.dangling', {1})",
                Guid.CreateVersion7(), Guid.NewGuid())));

        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task RollingBackPastB04_IsRefusedWhileTheAuditLogHasEntries()
    {
        await factory.SignedInAsync(); // writes audit entries

        await using var scope = factory.Services.CreateAsyncScope();
        var migrator = scope.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>();

        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            migrator.MigrateAsync("AddIdentityAndSessions"));
        Assert.Contains("Refusing to drop audit_logs", exception.MessageText);
        await factory.UpdateDatabaseAsync(async db => Assert.True(await db.AuditLogs.AnyAsync()));
    }

    // ---------- CLI ----------

    [Theory]
    [InlineData("--password", "secret-password-123")]
    [InlineData("--email", "not-an-email@")]
    public async Task CreateAdmin_RejectsUnknownOptionsAndInvalidEmails(string option, string value)
    {
        await factory.MigrateAsync();
        string[] args = option == "--email"
            ? ["--clinic", "C", "--name", "N", "--email", value]
            : ["--clinic", "C", "--name", "N", "--email", NewEmail(), option, value];

        var exitCode = await CreateAdminCommand.RunAsync(factory.Services, args, new StringReader("a-long-enough-password"), new StringWriter());

        Assert.Equal(2, exitCode);
    }
}
