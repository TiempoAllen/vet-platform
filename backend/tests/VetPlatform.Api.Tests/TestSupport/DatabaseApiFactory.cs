using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Tests.TestSupport;

/// <summary>
/// An API host backed by its own empty PostgreSQL database (see <see cref="TestPostgres"/>), dropped on dispose.
/// The schema is not created automatically: call <see cref="MigrateAsync"/> when a test needs it
/// (<see cref="CreateUserAsync"/> does).
/// </summary>
public class DatabaseApiFactory : ApiFactory, IAsyncLifetime
{
    private string? _connectionString;

    protected override string? ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("The test database has not been created yet.");

    public async Task InitializeAsync() => _connectionString = await TestPostgres.CreateDatabaseAsync();

    public async Task MigrateAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    /// <summary>Creates a user (in a new clinic unless one is given) whose password is <paramref name="password"/>.</summary>
    public async Task<User> CreateUserAsync(
        string email,
        string password,
        UserStatus status = UserStatus.Active,
        ClinicStatus clinicStatus = ClinicStatus.Active,
        Clinic? clinic = null,
        params Role[] roles)
    {
        await MigrateAsync();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        if (clinic is null)
        {
            clinic = new Clinic { Name = "Clinic for " + email, Status = clinicStatus };
            db.Clinics.Add(clinic);
        }

        var user = new User
        {
            ClinicId = clinic.Id,
            Name = "Test User",
            Email = User.NormalizeEmail(email),
            PasswordHash = "",
            Status = status,
            Roles = roles.Select(role => new UserRole { Role = role, CreatedAt = Clock.GetUtcNow() }).ToList(),
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);

        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Creates a new clinic with one user holding <paramref name="roles"/>, signed in on a ready client.</summary>
    public async Task<(HttpClient Client, User User)> SignedInAsync(params Role[] roles)
    {
        var user = await CreateUserAsync($"user-{Guid.NewGuid():N}@example.com", TestPassword, roles: roles);
        return (await SignInAsync(user), user);
    }

    /// <summary>Creates another user in <paramref name="colleague"/>'s clinic.</summary>
    public Task<User> CreateColleagueAsync(User colleague, UserStatus status = UserStatus.Active, params Role[] roles) =>
        CreateUserAsync(
            $"user-{Guid.NewGuid():N}@example.com", TestPassword, status,
            clinic: new Clinic { Id = colleague.ClinicId, Name = "existing" }, roles: roles);

    /// <summary>Signs an existing user (created with <see cref="TestPassword"/>) in on a new client.</summary>
    public async Task<HttpClient> SignInAsync(User user)
    {
        var client = await CreateClientWithCsrfAsync();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = TestPassword });
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Test sign-in failed with {response.StatusCode}.");
        }

        client.UseCsrfTokenFrom(response);
        return client;
    }

    public const string TestPassword = "Correct-Horse-9";

    /// <summary>Audit entries about an entity, oldest first.</summary>
    public async Task<List<AuditLog>> AuditEntriesAsync(Guid entityId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .Where(entry => entry.EntityId == entityId)
            .OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.Id)
            .ToListAsync();
    }

    /// <summary>Runs <paramref name="action"/> against a fresh <see cref="AppDbContext"/>, then saves.</summary>
    public async Task UpdateDatabaseAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        if (_connectionString is not null)
        {
            await TestPostgres.DropDatabaseAsync(_connectionString);
        }
    }
}

/// <summary>A database-backed host with the production-like sign-in rate limit of 3 attempts per minute.</summary>
public sealed class RateLimitedDatabaseApiFactory : DatabaseApiFactory
{
    public const int AttemptsPerMinute = 3;

    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Auth:LoginAttemptsPerMinute"] = AttemptsPerMinute.ToString(),
    };
}
