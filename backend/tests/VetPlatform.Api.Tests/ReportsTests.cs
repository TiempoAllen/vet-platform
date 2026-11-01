using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>Operational reports (B-23) against real PostgreSQL.</summary>
[Trait("Category", "Database")]
public sealed class ReportsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private sealed record Clinic(HttpClient Admin, HttpClient Desk, HttpClient VetClient, User VetA, User VetB, Guid Dog, Guid Cat);

    private async Task<Clinic> ClinicAsync(string? timeZone = null)
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        var vetA = await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian);
        var vetB = await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian);
        if (timeZone is not null)
        {
            await OkAsync(await admin.PatchAsJsonAsync("/api/clinic", new { timeZone }));
        }

        return new Clinic(admin, desk, await factory.SignInAsync(vetA), vetA, vetB, await PatientAsync(desk, "Rex", "Dog"), await PatientAsync(desk, "Tom", "Cat"));
    }

    private static async Task<Guid> PatientAsync(HttpClient desk, string name, string species)
    {
        var client = await OkAsync(await desk.PostAsJsonAsync("/api/clients", new { firstName = "Jane", lastName = name }), HttpStatusCode.Created);
        return (await OkAsync(await desk.PostAsJsonAsync("/api/patients", new { clientId = client.GetProperty("id").GetGuid(), name, species }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Seeds an appointment with the timestamps its status needs (booking rules are not under test here).</summary>
    private Task SeedAsync(Guid patientId, User vet, string startUtc, string status, string? cancelledBy = null) =>
        factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO appointments (id, clinic_id, patient_id, client_id, veterinarian_id, start_at, end_at, type, status, overridden_conflicts, " +
            "checked_in_at, completed_at, no_show_at, cancelled_at, cancelled_by, created_at, updated_at) " +
            "SELECT gen_random_uuid(), p.clinic_id, p.id, p.client_id, {1}, {2}::timestamptz, {2}::timestamptz + interval '30 minutes', 'Consultation', {3}, '{{}}', " +
            "CASE WHEN {3} IN ('CheckedIn', 'Completed') THEN {2}::timestamptz END, CASE WHEN {3} = 'Completed' THEN {2}::timestamptz + interval '20 minutes' END, " +
            "CASE WHEN {3} = 'NoShow' THEN {2}::timestamptz + interval '15 minutes' END, CASE WHEN {3} = 'Cancelled' THEN {2}::timestamptz - interval '1 day' END, " +
            "{4}, now(), now() FROM patients p WHERE p.id = {0}",
            patientId, vet.Id, startUtc, status,
            new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)cancelledBy ?? DBNull.Value }));

    private static int Int(JsonElement e, string p) => e.GetProperty(p).GetInt32();

    // ---------- Appointments ----------

    [Fact]
    public async Task Appointments_AreCountedByStatus_Veterinarian_AndPeriod()
    {
        var clinic = await ClinicAsync();
        await SeedAsync(clinic.Dog, clinic.VetA, "2026-05-04T09:00:00Z", "Completed");
        await SeedAsync(clinic.Cat, clinic.VetA, "2026-05-04T10:00:00Z", "NoShow");
        await SeedAsync(clinic.Dog, clinic.VetB, "2026-05-05T09:00:00Z", "Cancelled", "Client");
        await SeedAsync(clinic.Cat, clinic.VetA, "2026-05-06T09:00:00Z", "Cancelled", "Clinic");
        await SeedAsync(clinic.Dog, clinic.VetB, "2026-05-11T09:00:00Z", "Scheduled");
        await SeedAsync(clinic.Cat, clinic.VetB, "2026-05-12T09:00:00Z", "CheckedIn");
        await SeedAsync(clinic.Dog, clinic.VetA, "2026-06-01T09:00:00Z", "Completed"); // outside the period

        var report = await OkAsync(await clinic.Admin.GetAsync("/api/reports/appointments?from=2026-05-01&to=2026-05-31&groupBy=Week"));
        Assert.Equal(["from", "to", "groupBy", "timeZone", "totals", "byVeterinarian", "buckets"], report.EnumerateObject().Select(p => p.Name));
        var totals = report.GetProperty("totals");
        Assert.Equal(
            ["total", "scheduled", "checkedIn", "completed", "cancelled", "cancelledByClient", "cancelledByClinic", "noShow", "noShowRate", "cancellationRate"],
            totals.EnumerateObject().Select(p => p.Name));
        Assert.Equal((6, 1, 1, 1, 2, 1, 1, 1), (Int(totals, "total"), Int(totals, "scheduled"), Int(totals, "checkedIn"), Int(totals, "completed"),
            Int(totals, "cancelled"), Int(totals, "cancelledByClient"), Int(totals, "cancelledByClinic"), Int(totals, "noShow")));
        Assert.Equal((0.25m, 0.5m), (totals.GetProperty("noShowRate").GetDecimal(), totals.GetProperty("cancellationRate").GetDecimal()));
        Assert.Equal(("Week", "UTC"), (report.GetProperty("groupBy").GetString(), report.GetProperty("timeZone").GetString()));

        // Weeks run Monday to Sunday; the first and last are clipped to the period.
        var buckets = report.GetProperty("buckets").EnumerateArray().ToList();
        Assert.Equal(["2026-05-01", "2026-05-04", "2026-05-11", "2026-05-18", "2026-05-25"], buckets.Select(b => b.GetProperty("start").GetString()));
        Assert.Equal(["2026-05-03", "2026-05-10", "2026-05-17", "2026-05-24", "2026-05-31"], buckets.Select(b => b.GetProperty("end").GetString()));
        Assert.Equal([0, 4, 2, 0, 0], buckets.Select(b => Int(b.GetProperty("counts"), "total")));
        Assert.Equal(JsonValueKind.Null, buckets[0].GetProperty("counts").GetProperty("noShowRate").ValueKind);

        var byVet = report.GetProperty("byVeterinarian").EnumerateArray().ToDictionary(v => v.GetProperty("veterinarian").GetProperty("id").GetGuid());
        Assert.Equal((3, 1, 1), (Int(byVet[clinic.VetA.Id].GetProperty("counts"), "total"), Int(byVet[clinic.VetA.Id].GetProperty("counts"), "completed"), Int(byVet[clinic.VetA.Id].GetProperty("counts"), "noShow")));
        Assert.Equal(3, Int(byVet[clinic.VetB.Id].GetProperty("counts"), "total"));

        var daily = await OkAsync(await clinic.Admin.GetAsync("/api/reports/appointments?from=2026-05-01&to=2026-05-31"));
        Assert.Equal(31, daily.GetProperty("buckets").GetArrayLength());
        var monthly = await OkAsync(await clinic.Admin.GetAsync($"/api/reports/appointments?from=2026-04-15&to=2026-06-15&groupBy=Month&veterinarianId={clinic.VetA.Id}"));
        Assert.Equal([0, 3, 1], monthly.GetProperty("buckets").EnumerateArray().Select(b => Int(b.GetProperty("counts"), "total")));
        Assert.Equal(["2026-04-15", "2026-05-01", "2026-06-01"], monthly.GetProperty("buckets").EnumerateArray().Select(b => b.GetProperty("start").GetString()));
        Assert.Equal(4, Int(monthly.GetProperty("totals"), "total"));
    }

    [Fact]
    public async Task Days_FollowTheClinicTimeZone()
    {
        var clinic = await ClinicAsync("America/New_York");
        await SeedAsync(clinic.Dog, clinic.VetA, "2026-03-08T02:00:00Z", "Completed"); // 21:00 on 7 March in New York

        var report = await OkAsync(await clinic.Admin.GetAsync("/api/reports/appointments?from=2026-03-07&to=2026-03-08"));
        Assert.Equal([1, 0], report.GetProperty("buckets").EnumerateArray().Select(b => Int(b.GetProperty("counts"), "total")));
        Assert.Equal("America/New_York", report.GetProperty("timeZone").GetString());
    }

    // ---------- Patients seen ----------

    [Fact]
    public async Task PatientsSeen_CountsDistinctPatients_NewAndReturning()
    {
        var clinic = await ClinicAsync();
        await SeedAsync(clinic.Cat, clinic.VetB, "2026-04-20T09:00:00Z", "Completed"); // before the period: the cat returns
        await SeedAsync(clinic.Dog, clinic.VetA, "2026-05-04T09:00:00Z", "Completed"); // the dog's first visit
        await SeedAsync(clinic.Cat, clinic.VetA, "2026-05-05T09:00:00Z", "Completed");
        await SeedAsync(clinic.Dog, clinic.VetB, "2026-05-12T09:00:00Z", "Completed");
        await SeedAsync(clinic.Cat, clinic.VetA, "2026-05-13T09:00:00Z", "NoShow"); // not seen

        var report = await OkAsync(await clinic.Admin.GetAsync("/api/reports/patients-seen?from=2026-05-01&to=2026-05-31&groupBy=Week"));
        Assert.Equal(["from", "to", "groupBy", "timeZone", "totals", "bySpecies", "byVeterinarian", "buckets"], report.EnumerateObject().Select(p => p.Name));
        var totals = report.GetProperty("totals");
        Assert.Equal(["patients", "newPatients", "returningPatients", "visits"], totals.EnumerateObject().Select(p => p.Name));
        Assert.Equal((2, 1, 1, 3), (Int(totals, "patients"), Int(totals, "newPatients"), Int(totals, "returningPatients"), Int(totals, "visits")));

        var buckets = report.GetProperty("buckets").EnumerateArray().Select(b => b.GetProperty("counts")).ToList();
        Assert.Equal((2, 1, 1, 2), (Int(buckets[1], "patients"), Int(buckets[1], "newPatients"), Int(buckets[1], "returningPatients"), Int(buckets[1], "visits")));
        Assert.Equal((1, 0, 1, 1), (Int(buckets[2], "patients"), Int(buckets[2], "newPatients"), Int(buckets[2], "returningPatients"), Int(buckets[2], "visits")));

        var species = report.GetProperty("bySpecies").EnumerateArray().ToDictionary(s => s.GetProperty("species").GetString()!, s => s.GetProperty("counts"));
        Assert.Equal((1, 1, 2), (Int(species["Dog"], "patients"), Int(species["Dog"], "newPatients"), Int(species["Dog"], "visits")));
        Assert.Equal((1, 0, 1), (Int(species["Cat"], "patients"), Int(species["Cat"], "newPatients"), Int(species["Cat"], "visits")));

        // Only veterinarian B's visits; the dog is still new (its first visit, with A, falls in the period).
        var forB = await OkAsync(await clinic.Admin.GetAsync($"/api/reports/patients-seen?from=2026-05-01&to=2026-05-31&veterinarianId={clinic.VetB.Id}"));
        Assert.Equal((1, 1, 1), (Int(forB.GetProperty("totals"), "patients"), Int(forB.GetProperty("totals"), "newPatients"), Int(forB.GetProperty("totals"), "visits")));
    }

    // ---------- Rules ----------

    [Theory]
    [InlineData("to=2026-05-31")]
    [InlineData("from=2026-05-31&to=2026-05-01")]
    [InlineData("from=2026-01-01&to=2027-01-02")]
    [InlineData("from=2026-05-01&to=2026-05-31&groupBy=week")]
    [InlineData("from=1999-12-31&to=2000-01-02")]
    [InlineData("from=2026-05-01&to=not-a-date")]
    public async Task InvalidPeriods_Are422(string query)
    {
        var clinic = await ClinicAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Admin.GetAsync("/api/reports/appointments?" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Admin.GetAsync("/api/reports/patients-seen?" + query)).StatusCode);
    }

    [Fact]
    public async Task Reports_AreForAdministrators_AndStayInTheClinic()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        await SeedAsync(other.Dog, other.VetA, "2026-05-04T09:00:00Z", "Completed");
        const string url = "/api/reports/appointments?from=2026-01-01&to=2026-12-31";

        Assert.Equal(0, Int((await OkAsync(await clinic.Admin.GetAsync(url))).GetProperty("totals"), "total"));
        Assert.Equal(0, Int((await OkAsync(await clinic.Admin.GetAsync($"/api/reports/patients-seen?from=2026-01-01&to=2026-12-31&veterinarianId={other.VetA.Id}"))).GetProperty("totals"), "visits"));
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.VetClient.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(url)).StatusCode);
        Assert.Contains(Permissions.ReportsRead, (await clinic.Admin.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }
}
