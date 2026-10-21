using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Patient history timeline (B-07) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class PatientHistoryTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private static async Task<Guid> PostIdAsync(HttpClient http, string path, object body)
    {
        var response = await http.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>A receptionist in a new clinic with one client ("Jane Doe") and one patient ("Rex").</summary>
    private async Task<(HttpClient Http, User User, Guid ClientId, Guid PatientId)> ClinicWithPatientAsync()
    {
        var (http, user) = await factory.SignedInAsync(Role.Receptionist);
        var clientId = await PostIdAsync(http, "/api/clients", new { firstName = "Jane", lastName = "Doe" });
        var patientId = await PostIdAsync(http, "/api/patients", new { clientId, name = "Rex", species = "Dog" });
        return (http, user, clientId, patientId);
    }

    private static Task<JsonElement> HistoryAsync(HttpClient http, Guid patientId, string query = "") =>
        http.GetFromJsonAsync<JsonElement>($"/api/patients/{patientId}/history{query}");

    private Task AddAuditEntryAsync(Guid? clinicId, Guid? actorUserId, Guid patientId, string? metadata, DateTimeOffset occurredAt) =>
        factory.UpdateDatabaseAsync(db =>
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = AuditActions.PatientCreated,
                ClinicId = clinicId,
                ActorUserId = actorUserId,
                EntityType = "Patient",
                EntityId = patientId,
                Metadata = metadata,
                OccurredAt = occurredAt,
            });
            return Task.CompletedTask;
        });

    [Fact]
    public async Task NewPatient_HasARegisteredEntry_WithActorAndOwner()
    {
        var (http, user, clientId, patientId) = await ClinicWithPatientAsync();

        var history = await HistoryAsync(http, patientId);

        var entry = Assert.Single(history.GetProperty("data").EnumerateArray());
        Assert.Equal("PatientRegistered", entry.GetProperty("type").GetString());
        Assert.Equal(user.Id, entry.GetProperty("actor").GetProperty("id").GetGuid());
        Assert.Equal("Test User", entry.GetProperty("actor").GetProperty("name").GetString());
        var client = entry.GetProperty("details").GetProperty("client");
        Assert.Equal(clientId, client.GetProperty("id").GetGuid());
        Assert.Equal("Doe", client.GetProperty("lastName").GetString());
        Assert.Equal(1, history.GetProperty("pagination").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Entry_HasExactlyTheDocumentedFields()
    {
        var (http, _, _, patientId) = await ClinicWithPatientAsync();

        var entry = (await HistoryAsync(http, patientId)).GetProperty("data")[0];

        Assert.Equal(["id", "type", "occurredAt", "actor", "details"], entry.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["id", "name"], entry.GetProperty("actor").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["client"], entry.GetProperty("details").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Updates_DoNotAppearYet()
    {
        var (http, _, _, patientId) = await ClinicWithPatientAsync();
        await http.PatchAsJsonAsync($"/api/patients/{patientId}", new { name = "Max", weightKg = 20 });

        var history = await HistoryAsync(http, patientId);

        Assert.Equal(["PatientRegistered"], history.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task OwnerShown_IsTheOwnerAtRegistration_WithCurrentName()
    {
        var (http, _, firstOwner, patientId) = await ClinicWithPatientAsync();
        var secondOwner = await PostIdAsync(http, "/api/clients", new { firstName = "John", lastName = "Smith" });
        await http.PatchAsJsonAsync($"/api/patients/{patientId}", new { clientId = secondOwner });
        await http.PatchAsJsonAsync($"/api/clients/{firstOwner}", new { lastName = "Doe-Brown" });

        var client = (await HistoryAsync(http, patientId)).GetProperty("data")[0].GetProperty("details").GetProperty("client");

        Assert.Equal(firstOwner, client.GetProperty("id").GetGuid());
        Assert.Equal("Doe-Brown", client.GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task EntriesWithoutAnActorOrOwner_HaveNulls_AndAreNewestFirst()
    {
        var (http, user, _, patientId) = await ClinicWithPatientAsync();
        var later = factory.Clock.GetUtcNow().AddMinutes(5);
        await AddAuditEntryAsync(user.ClinicId, actorUserId: null, patientId, metadata: null, later);
        await AddAuditEntryAsync(user.ClinicId, actorUserId: null, patientId, metadata: """{"clientId": 42}""", later.AddMinutes(1));

        var data = (await HistoryAsync(http, patientId)).GetProperty("data").EnumerateArray().ToList();

        Assert.Equal(3, data.Count);
        Assert.True(data[0].GetProperty("occurredAt").GetDateTimeOffset() > data[1].GetProperty("occurredAt").GetDateTimeOffset());
        Assert.True(data[1].GetProperty("occurredAt").GetDateTimeOffset() > data[2].GetProperty("occurredAt").GetDateTimeOffset());
        foreach (var entry in data.Take(2))
        {
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("actor").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("details").GetProperty("client").ValueKind);
        }
    }

    [Fact]
    public async Task Pages_AreStable_AndValidated()
    {
        var (http, user, _, patientId) = await ClinicWithPatientAsync();
        for (var i = 1; i <= 2; i++)
        {
            await AddAuditEntryAsync(user.ClinicId, user.Id, patientId, null, factory.Clock.GetUtcNow().AddMinutes(i));
        }

        var first = await HistoryAsync(http, patientId, "?pageSize=2");
        var second = await HistoryAsync(http, patientId, "?pageSize=2&page=2");

        Assert.Equal(3, first.GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.Equal(2, first.GetProperty("data").GetArrayLength());
        Assert.Equal(1, second.GetProperty("data").GetArrayLength());
        var ids = first.GetProperty("data").EnumerateArray().Concat(second.GetProperty("data").EnumerateArray())
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(3, ids.Distinct().Count());

        var invalid = await http.GetAsync($"/api/patients/{patientId}/history?pageSize=101");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Contains((await ErrorOf(invalid)).Details, d => d.Field == "pageSize");
    }

    // ---------- Permissions and tenant isolation ----------

    [Theory]
    [InlineData(Role.Inventory)]
    [InlineData(null)]
    public async Task UsersWithoutPatientAccess_CannotReadHistory(Role? role)
    {
        var (_, _, _, patientId) = await ClinicWithPatientAsync();
        var (http, _) = await factory.SignedInAsync(role is { } r ? [r] : []);

        var response = await http.GetAsync($"/api/patients/{patientId}/history");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.Veterinarian)]
    [InlineData(Role.Nurse)]
    public async Task ClinicStaff_CanReadHistory(Role role)
    {
        var (_, user, _, patientId) = await ClinicWithPatientAsync();
        var colleague = await factory.SignInAsync(await factory.CreateColleagueAsync(user, roles: role));

        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync($"/api/patients/{patientId}/history")).StatusCode);
    }

    [Fact]
    public async Task OtherClinicsAndUnknownPatients_Are404()
    {
        var (httpA, _, _, _) = await ClinicWithPatientAsync();
        var (_, _, _, patientB) = await ClinicWithPatientAsync();

        foreach (var id in new[] { patientB, Guid.NewGuid() })
        {
            var response = await httpA.GetAsync($"/api/patients/{id}/history");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ErrorCodes.NotFound, (await ErrorOf(response)).Code);
        }
    }

    [Fact]
    public async Task AuditEntriesOfAnotherClinic_NeverAppear_EvenForTheSamePatientId()
    {
        var (http, _, _, patientId) = await ClinicWithPatientAsync();
        var (_, otherUser, otherClient, _) = await ClinicWithPatientAsync();
        await AddAuditEntryAsync(
            otherUser.ClinicId, otherUser.Id, patientId, $$"""{"clientId": "{{otherClient}}"}""", factory.Clock.GetUtcNow().AddMinutes(1));
        await AddAuditEntryAsync(null, null, patientId, null, factory.Clock.GetUtcNow().AddMinutes(2));

        var data = (await HistoryAsync(http, patientId)).GetProperty("data").EnumerateArray().ToList();

        var entry = Assert.Single(data);
        Assert.NotEqual(otherUser.Id, entry.GetProperty("actor").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AuditQueriesWithoutACurrentClinic_ReturnNothing()
    {
        await ClinicWithPatientAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        Assert.Null(tenant.ClinicId);
        Assert.Empty(await db.ForCurrentClinic(tenant).ToListAsync()); // fail closed
        Assert.NotEmpty(await db.AuditLogs.ToListAsync());
    }
}
