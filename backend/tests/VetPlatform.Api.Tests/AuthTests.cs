using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Cookie-session sign-in against real PostgreSQL. Each test uses its own account.</summary>
[Trait("Category", "Database")]
public sealed class AuthTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private const string Password = "Correct-Horse-9";

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        client.UseCsrfTokenFrom(response);
        return response;
    }

    private async Task<(HttpClient Client, string SessionCookie)> SignedInClientAsync(string email)
    {
        var client = await factory.CreateClientWithCsrfAsync();
        var response = await LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = CsrfClientExtensions.FindCookie(response, AuthServiceCollectionExtensions.SessionCookieName);
        return (client, cookie!);
    }

    /// <summary>Sends a request carrying only the given session cookie, like an attacker replaying a stolen cookie.</summary>
    private async Task<HttpStatusCode> MeWithCookieAsync(string sessionCookie)
    {
        var client = factory.Server.CreateClient();
        client.BaseAddress = new Uri("https://localhost");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", $"{AuthServiceCollectionExtensions.SessionCookieName}={sessionCookie}");
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task Login_WithValidCredentials_SetsHardenedSessionCookie_AndMeReturnsTheUser()
    {
        var email = NewEmail();
        var user = await factory.CreateUserAsync(email, Password);
        var client = await factory.CreateClientWithCsrfAsync();

        var response = await LoginAsync(client, email, Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = CsrfClientExtensions.FindSetCookieHeader(response, AuthServiceCollectionExtensions.SessionCookieName);
        Assert.NotNull(cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase); // browser-session cookie

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(user.Id.ToString(), me.GetProperty("id").GetString());
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.Equal(user.ClinicId.ToString(), me.GetProperty("clinic").GetProperty("id").GetString());
        Assert.Equal(
            ["id", "name", "email", "clinic"],
            me.EnumerateObject().Select(property => property.Name)); // nothing else (no hash, no lockout state)
    }

    [Fact]
    public async Task Login_IgnoresEmailCaseAndSurroundingSpaces()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var client = await factory.CreateClientWithCsrfAsync();

        var response = await LoginAsync(client, "  " + email.ToUpperInvariant() + " ", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FailedLogins_AllLookIdentical_SoAccountsCannotBeEnumerated()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var disabled = NewEmail();
        await factory.CreateUserAsync(disabled, Password, status: UserStatus.Disabled);
        var suspendedClinic = NewEmail();
        await factory.CreateUserAsync(suspendedClinic, Password, clinicStatus: ClinicStatus.Suspended);
        var locked = NewEmail();
        await factory.CreateUserAsync(locked, Password);
        var client = await factory.CreateClientWithCsrfAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await LoginAsync(client, locked, "wrong-password");
        }

        var attempts = new[]
        {
            await LoginAsync(client, email, "wrong-password"),
            await LoginAsync(client, NewEmail(), Password),
            await LoginAsync(client, disabled, Password),
            await LoginAsync(client, suspendedClinic, Password),
            await LoginAsync(client, locked, Password),
        };

        foreach (var response in attempts)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            Assert.Equal(ErrorCodes.Unauthenticated, error!.Error.Code);
            Assert.Equal("Email or password is incorrect.", error.Error.Message);
            Assert.Null(CsrfClientExtensions.FindCookie(response, AuthServiceCollectionExtensions.SessionCookieName));
        }
    }

    [Fact]
    public async Task RepeatedWrongPasswords_LockTheAccountTemporarily()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var client = await factory.CreateClientWithCsrfAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, email, "wrong-password")).StatusCode);
        }

        // Locked: even the right password is refused, with the same generic response.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, email, Password)).StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, Password)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentWrongPasswords_StillLockTheAccount()
    {
        var email = NewEmail();
        var user = await factory.CreateUserAsync(email, Password);
        var clients = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => factory.CreateClientWithCsrfAsync()));

        var responses = await Task.WhenAll(clients.Select(client => LoginAsync(client, email, "wrong-password")));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(clients[0], email, Password)).StatusCode);

        await factory.UpdateDatabaseAsync(async db =>
        {
            var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
            Assert.True(stored.LockedUntil > factory.Clock.GetUtcNow());
        });
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsTheFailedAttemptCount()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var client = await factory.CreateClientWithCsrfAsync();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await LoginAsync(client, email, "wrong-password");
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, email, "wrong-password")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, Password)).StatusCode); // not locked
    }

    [Fact]
    public async Task Logout_RevokesTheSessionServerSide_SoAReplayedCookieIsRejected()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var (client, sessionCookie) = await SignedInClientAsync(email);

        var logout = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(sessionCookie));
    }

    [Fact]
    public async Task Logout_WithoutASession_StillSucceeds()
    {
        var client = await factory.CreateClientWithCsrfAsync();

        var response = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task CsrfToken_IsReissuedAtSignIn_AndThePreSignInTokenNoLongerWorks()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var client = await factory.CreateClientWithCsrfAsync();
        var anonymousToken = client.DefaultRequestHeaders.GetValues("X-XSRF-TOKEN").Single();

        await LoginAsync(client, email, Password);
        var signedInToken = client.DefaultRequestHeaders.GetValues("X-XSRF-TOKEN").Single();
        Assert.NotEqual(anonymousToken, signedInToken);

        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", anonymousToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/logout", content: null)).StatusCode);
    }

    [Fact]
    public async Task CsrfToken_IsBoundToTheSession_NotJustTheUser()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var otherEmail = NewEmail();
        await factory.CreateUserAsync(otherEmail, Password);
        var (firstBrowser, _) = await SignedInClientAsync(email);
        var (secondBrowser, _) = await SignedInClientAsync(email);
        var (otherUser, _) = await SignedInClientAsync(otherEmail);
        var firstToken = firstBrowser.DefaultRequestHeaders.GetValues("X-XSRF-TOKEN").Single();

        foreach (var client in new[] { secondBrowser, otherUser })
        {
            client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", firstToken);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/logout", content: null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await firstBrowser.PostAsync("/api/auth/logout", content: null)).StatusCode);
    }

    [Fact]
    public async Task AfterSessionExpiry_TheStaleCsrfTokenIsRejected_AndRefreshingItLetsTheUserSignInAgain()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var (client, _) = await SignedInClientAsync(email);
        factory.Clock.Advance(TimeSpan.FromHours(3));

        var stale = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        var error = await stale.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ErrorCodes.CsrfTokenInvalid, error!.Error.Code);

        await client.RefreshCsrfTokenAsync();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, Password)).StatusCode);
    }

    /// <summary>
    /// A cookie forged with the app's own keys (as if the key ring leaked) that pairs a real session with another
    /// identity must be rejected: the session row, not the cookie's claims, decides who the caller is.
    /// </summary>
    [Fact]
    public async Task ForgedCookie_PairingAValidSessionWithAnotherIdentity_IsRejected()
    {
        var attacker = await factory.CreateUserAsync(NewEmail(), Password);
        var victim = await factory.CreateUserAsync(NewEmail(), Password);
        await SignedInClientAsync(attacker.Email);
        var attackerSession = Guid.Empty;
        await factory.UpdateDatabaseAsync(async db => attackerSession = await db.UserSessions
            .Where(s => s.UserId == attacker.Id).Select(s => s.Id).SingleAsync());

        // Control: a forged cookie with the attacker's own matching identity works, so the rejections below are real.
        Assert.Equal(HttpStatusCode.OK, await MeWithCookieAsync(ForgeCookie(attacker.Id, attackerSession, attacker.ClinicId)));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(ForgeCookie(victim.Id, attackerSession, victim.ClinicId)));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(ForgeCookie(attacker.Id, attackerSession, victim.ClinicId)));
    }

    private string ForgeCookie(Guid userId, Guid sessionId, Guid clinicId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(AuthClaims.SessionId, sessionId.ToString()),
                new Claim(AuthClaims.ClinicId, clinicId.ToString()),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));
        var cookieOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties { ExpiresUtc = factory.Clock.GetUtcNow().AddHours(1) };
        return cookieOptions.TicketDataFormat.Protect(
            new AuthenticationTicket(principal, properties, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    [Fact]
    public async Task SigningInAgain_RevokesThePreviousSessionOfThatBrowser()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var (client, firstSession) = await SignedInClientAsync(email);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, Password)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(firstSession));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_ExpiresAfterIdleTimeout_ButActivityKeepsItAlive()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var (client, _) = await SignedInClientAsync(email);

        factory.Clock.Advance(TimeSpan.FromMinutes(110));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(110));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(121));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_EndsAtItsAbsoluteLifetime_EvenWhenActive()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var (client, _) = await SignedInClientAsync(email);

        for (var hour = 0; hour < 11; hour++)
        {
            factory.Clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        }

        factory.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task DisablingAUser_EndsTheirExistingSessionsImmediately()
    {
        var email = NewEmail();
        var user = await factory.CreateUserAsync(email, Password);
        var (client, _) = await SignedInClientAsync(email);

        await factory.UpdateDatabaseAsync(db => db.Users
            .Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(u => u.Status, UserStatus.Disabled)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task SuspendingTheClinic_EndsItsUsersSessionsImmediately()
    {
        var email = NewEmail();
        var user = await factory.CreateUserAsync(email, Password);
        var (client, _) = await SignedInClientAsync(email);

        await factory.UpdateDatabaseAsync(db => db.Clinics
            .Where(c => c.Id == user.ClinicId)
            .ExecuteUpdateAsync(update => update.SetProperty(c => c.Status, ClinicStatus.Suspended)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Credentials_AreNeverWrittenToLogs()
    {
        var email = NewEmail();
        await factory.CreateUserAsync(email, Password);
        var client = await factory.CreateClientWithCsrfAsync();

        await LoginAsync(client, email, "Wrong-Secret-Value-1");
        await LoginAsync(client, email, Password);

        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("signed in"));
        Assert.DoesNotContain(factory.Logs.Entries, entry =>
            entry.Contains("Wrong-Secret-Value-1") || entry.Contains(Password) || entry.Contains(email));
    }

    [Fact]
    public async Task Database_RejectsAnEmailDifferingOnlyInCase_EvenWhenAWriterSkipsNormalisation()
    {
        var email = NewEmail();
        var existing = await factory.CreateUserAsync(email, Password);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => factory.UpdateDatabaseAsync(db =>
        {
            db.Users.Add(new User
            {
                ClinicId = existing.ClinicId,
                Name = "Duplicate",
                Email = email.ToUpperInvariant(), // deliberately not normalised
                PasswordHash = "x",
            });
            return Task.CompletedTask;
        }));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, (exception.InnerException as PostgresException)?.SqlState);
    }

    [Fact]
    public async Task UnnormalisedStoredEmail_StillSignsIn()
    {
        var email = NewEmail();
        var user = await factory.CreateUserAsync(email, Password);
        await factory.UpdateDatabaseAsync(db => db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(u => u.Email, email.ToUpperInvariant())));
        var client = await factory.CreateClientWithCsrfAsync();

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, Password)).StatusCode);
    }

    [Theory]
    [InlineData("UPDATE users SET status = 'active' WHERE id = {0}")]
    [InlineData("UPDATE users SET failed_login_count = -1 WHERE id = {0}")]
    public async Task Database_RejectsInvalidUserValues(string sql)
    {
        var user = await factory.CreateUserAsync(NewEmail(), Password);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db =>
            db.Database.ExecuteSqlRawAsync(sql, user.Id)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task NewRows_GetCreatedAndUpdatedTimestampsFromTheClock()
    {
        var user = await factory.CreateUserAsync(NewEmail(), Password);

        Assert.Equal(factory.Clock.GetUtcNow(), user.CreatedAt);
        Assert.Equal(user.CreatedAt, user.UpdatedAt);
    }
}

/// <summary>Sign-in throttling per client address. Separate host: the limit is shared by the whole test server.</summary>
[Trait("Category", "Database")]
public sealed class LoginRateLimitTests(RateLimitedDatabaseApiFactory factory) : IClassFixture<RateLimitedDatabaseApiFactory>
{
    [Fact]
    public async Task TooManyLoginAttempts_Return429WithRetryAfter()
    {
        await factory.MigrateAsync();
        var client = await factory.CreateClientWithCsrfAsync();
        var body = new { email = "nobody@example.com", password = "x" };

        for (var attempt = 0; attempt < RateLimitedDatabaseApiFactory.AttemptsPerMinute; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", body)).StatusCode);
        }

        var limited = await client.PostAsJsonAsync("/api/auth/login", body);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        var error = await limited.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal(ErrorCodes.RateLimited, error!.Error.Code);
    }
}
