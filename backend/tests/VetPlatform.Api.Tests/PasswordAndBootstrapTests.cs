using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Features.Users;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Self-service password change and the create-admin bootstrap command (B-04).</summary>
[Trait("Category", "Database")]
public sealed class PasswordAndBootstrapTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private const string NewPassword = "a-brand-new-password";

    [Fact]
    public async Task ChangePassword_RequiresTheCurrentPassword()
    {
        var (client, _) = await factory.SignedInAsync();

        var response = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = "wrong-password-1", newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Contains(error!.Error.Details, detail => detail.Field == "currentPassword");
    }

    [Fact]
    public async Task ChangePassword_EnforcesThePasswordPolicy()
    {
        var (client, user) = await factory.SignedInAsync();

        foreach (var weak in new[] { "short", user.Email })
        {
            var response = await client.PostAsJsonAsync("/api/auth/password",
                new { currentPassword = DatabaseApiFactory.TestPassword, newPassword = weak });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            Assert.Contains(error!.Error.Details, detail => detail.Field == "newPassword");
        }
    }

    [Fact]
    public async Task ChangePassword_KeepsThisSession_SignsOutOthers_AndOnlyTheNewPasswordWorks()
    {
        var (client, user) = await factory.SignedInAsync();
        var otherBrowser = await factory.SignInAsync(user);

        var response = await client.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = DatabaseApiFactory.TestPassword, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherBrowser.GetAsync("/api/auth/me")).StatusCode);

        var fresh = await factory.CreateClientWithCsrfAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fresh.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = DatabaseApiFactory.TestPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await fresh.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = NewPassword })).StatusCode);

        Assert.Contains(await factory.AuditEntriesAsync(user.Id), entry => entry.Action == AuditActions.PasswordChanged);
    }

    [Fact]
    public async Task ChangePassword_RequiresSignIn()
    {
        var client = await factory.CreateClientWithCsrfAsync();

        var response = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = "x", newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateAdmin_CreatesAClinicAndAnAdministratorWhoCanManageUsers()
    {
        await factory.MigrateAsync();
        var email = $"owner-{Guid.NewGuid():N}@happypaws.example";
        var output = new StringWriter();

        var exitCode = await CreateAdminCommand.RunAsync(
            factory.Services,
            ["--clinic", "Happy Paws", "--name", "Jo Owner", "--email", email],
            new StringReader(NewPassword + Environment.NewLine),
            output);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(NewPassword, output.ToString());

        var client = await factory.CreateClientWithCsrfAsync();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.UseCsrfTokenFrom(login);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);

        await factory.UpdateDatabaseAsync(async db =>
        {
            var admin = await db.Users.IgnoreQueryFilters().Include(u => u.Roles).Include(u => u.Clinic)
                .SingleAsync(u => u.Email == email);
            Assert.Equal("Happy Paws", admin.Clinic!.Name);
            Assert.Equal([Role.Admin], admin.Roles.Select(role => role.Role));
            Assert.True(await db.AuditLogs.AnyAsync(entry =>
                entry.Action == AuditActions.UserCreated && entry.EntityId == admin.Id && entry.ClinicId == admin.ClinicId));
        });
    }

    [Theory]
    [InlineData(new[] { "--clinic", "Happy Paws", "--name", "Jo" }, "a-brand-new-password", 2)]         // missing email
    [InlineData(new[] { "--clinic", "Happy Paws", "--name", "Jo", "--email", "jo@x.example" }, "short", 2)]
    [InlineData(new[] { "--clinic", "Happy Paws", "--name", "Jo", "--email" }, "a-brand-new-password", 2)] // dangling flag
    public async Task CreateAdmin_RejectsBadInput_WithoutTouchingTheDatabase(string[] args, string password, int expectedExit)
    {
        await factory.MigrateAsync();

        var exitCode = await CreateAdminCommand.RunAsync(factory.Services, args, new StringReader(password), new StringWriter());

        Assert.Equal(expectedExit, exitCode);
    }

    [Fact]
    public async Task CreateAdmin_RefusesAnEmailThatIsAlreadyTaken()
    {
        var (_, existing) = await factory.SignedInAsync();

        var exitCode = await CreateAdminCommand.RunAsync(
            factory.Services,
            ["--clinic", "Another Clinic", "--name", "Copycat", "--email", existing.Email.ToUpperInvariant()],
            new StringReader(NewPassword),
            new StringWriter());

        Assert.Equal(1, exitCode);
    }
}
