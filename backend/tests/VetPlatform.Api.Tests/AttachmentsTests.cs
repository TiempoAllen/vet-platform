using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

/// <summary>Attachments of patients, consultations and clients (B-15) against real PostgreSQL and a temp file store.</summary>
[Trait("Category", "Database")]
public sealed class AttachmentsTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1];
    private static readonly byte[] WebP = [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBPVP8 "u8];
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n%%EOF\n"u8];

    private static async Task<ApiErrorBody> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error;

    private sealed record Clinic(
        HttpClient Admin, User AdminUser, HttpClient Vet, User VetUser, HttpClient Nurse, HttpClient Desk, Guid ClientId, Guid PatientId);

    private async Task<Clinic> ClinicAsync()
    {
        var (admin, adminUser) = await factory.SignedInAsync(Role.Admin);
        var vetUser = await factory.CreateColleagueAsync(adminUser, roles: Role.Veterinarian);
        var nurse = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Nurse));
        var desk = await factory.SignInAsync(await factory.CreateColleagueAsync(adminUser, roles: Role.Receptionist));
        var client = await desk.PostAsJsonAsync("/api/clients", new { firstName = "Jane", lastName = "Doe" });
        var clientId = (await client.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var patient = await desk.PostAsJsonAsync("/api/patients", new { clientId, name = "Rex", species = "Dog" });
        var patientId = (await patient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return new Clinic(admin, adminUser, await factory.SignInAsync(vetUser), vetUser, nurse, desk, clientId, patientId);
    }

    private static MultipartFormDataContent Form(byte[] bytes, string fileName = "scan.png", string clientType = "application/octet-stream", string? description = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(clientType);
        form.Add(file, "file", fileName);
        if (description is not null)
        {
            form.Add(new StringContent(description), "description");
        }

        return form;
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> UploadAsync(HttpClient http, string ownerPath, byte[]? bytes = null) =>
        (await OkAsync(await http.PostAsync($"{ownerPath}/attachments", Form(bytes ?? Png)), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<Guid> OpenConsultationAsync(Clinic clinic) =>
        (await OkAsync(await clinic.Vet.PostAsJsonAsync("/api/consultations", new { patientId = clinic.PatientId, veterinarianId = clinic.VetUser.Id }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

    // ---------- Upload and download ----------

    [Fact]
    public async Task ANurse_UploadsAPatientFile_AndItDownloadsSafely()
    {
        var clinic = await ClinicAsync();
        var bytes = Png.Concat(Enumerable.Repeat((byte)7, 5000)).ToArray();

        var response = await clinic.Nurse.PostAsync(
            $"/api/patients/{clinic.PatientId}/attachments",
            Form(bytes, fileName: @"C:\scans\../Lateral thorax.png", clientType: "text/html", description: " Lateral thorax "));

        var created = await OkAsync(response, HttpStatusCode.Created);
        Assert.Equal(
            ["id", "ownerType", "patientId", "consultationId", "clientId", "fileName", "contentType", "sizeBytes", "sha256",
             "description", "status", "withdrawnAt", "withdrawnBy", "withdrawReason", "uploadedBy", "createdAt", "updatedAt"],
            created.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Patient", created.GetProperty("ownerType").GetString());
        Assert.Equal("Lateral thorax.png", created.GetProperty("fileName").GetString());
        Assert.Equal("image/png", created.GetProperty("contentType").GetString()); // detected, not the client's text/html
        Assert.Equal(bytes.Length, created.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), created.GetProperty("sha256").GetString());
        Assert.Equal("Lateral thorax", created.GetProperty("description").GetString());
        var id = created.GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/attachments/{id}", response.Headers.Location!.ToString());

        var download = await clinic.Vet.GetAsync($"/api/attachments/{id}/content");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", download.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", download.Headers.CacheControl!.ToString());

        var read = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/attachments/{id}");
        Assert.Equal(created.GetRawText(), read.GetRawText());
        var entries = await factory.AuditEntriesAsync(id);
        Assert.Equal(
            [AuditActions.AttachmentUploaded, AuditActions.AttachmentDownloaded, AuditActions.AttachmentViewed],
            entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.DoesNotContain("thorax", e.Metadata));

        // The stored content sits under a random key, not the uploader's name.
        var stored = Directory.GetFiles(factory.FileStorageRoot, "*", SearchOption.AllDirectories);
        Assert.Contains(stored, path => File.ReadAllBytes(path).SequenceEqual(bytes));
        Assert.DoesNotContain(stored, path => path.Contains("thorax", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../../x-ray \"left\".png", "image/png", "x-ray left.png")]
    [InlineData(@"C:\scans\lab.pdf", "application/pdf", "lab.pdf")]
    [InlineData("tab\there.pdf", "application/pdf", "tabhere.pdf")]
    [InlineData("xray.hta", "image/jpeg", "xray.jpg")]
    [InlineData("report.html", "image/png", "report.png")]
    [InlineData("scan\u202Efdp.hta", "image/jpeg", "scanfdp.jpg")]
    [InlineData("   ", "application/pdf", "file.pdf")]
    [InlineData(null, "image/webp", "file.webp")]
    public void FileNames_AreReducedToASafeDownloadName_WithTheDetectedExtension(string? uploaded, string contentType, string expected) =>
        Assert.Equal(expected, VetPlatform.Api.Features.Attachments.FileSignatures.SafeFileName(uploaded, contentType));

    public static TheoryData<byte[], string> Accepted => new()
    {
        { Png, "image/png" }, { Jpeg, "image/jpeg" }, { WebP, "image/webp" }, { Pdf, "application/pdf" },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task AcceptedTypes_AreRecognisedByTheirContent(byte[] bytes, string expected)
    {
        var clinic = await ClinicAsync();

        var created = await OkAsync(await clinic.Nurse.PostAsync($"/api/patients/{clinic.PatientId}/attachments", Form(bytes, "no-extension")), HttpStatusCode.Created);

        Assert.Equal(expected, created.GetProperty("contentType").GetString());
    }

    public static TheoryData<byte[], string> Refused => new()
    {
        { "<html><script>alert(1)</script></html>"u8.ToArray(), "page.png" },
        { "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), "image.svg" },
        { [0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0], "tool.pdf" },
        { "plain text"u8.ToArray(), "notes.txt" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task OtherContent_Is415_WhateverItsName(byte[] bytes, string fileName)
    {
        var clinic = await ClinicAsync();

        var response = await clinic.Nurse.PostAsync($"/api/patients/{clinic.PatientId}/attachments", Form(bytes, fileName, "image/png"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedMediaType, (await ErrorOf(response)).Code);
        Assert.Empty(Directory.Exists(factory.FileStorageRoot)
            ? Directory.GetFiles(factory.FileStorageRoot, "*.tmp", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task MalformedUploads_AreRejected()
    {
        var clinic = await ClinicAsync();
        var url = $"/api/patients/{clinic.PatientId}/attachments";

        var empty = await clinic.Nurse.PostAsync(url, Form([]));
        Assert.Equal("file", Assert.Single((await ErrorOf(empty)).Details).Field);

        var noFile = new MultipartFormDataContent { { new StringContent("x"), "description" } };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Nurse.PostAsync(url, noFile)).StatusCode);

        var two = Form(Png);
        two.Add(new ByteArrayContent(Png), "file", "second.png");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Nurse.PostAsync(url, two)).StatusCode);

        var longDescription = await clinic.Nurse.PostAsync(url, Form(Png, description: new string('d', 501)));
        Assert.Equal("description", Assert.Single((await ErrorOf(longDescription)).Details).Field);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await clinic.Nurse.PostAsJsonAsync(url, new { file = "x" })).StatusCode);
    }

    [Fact]
    public async Task FilesOver20Megabytes_Are413()
    {
        var clinic = await ClinicAsync();
        var url = $"/api/patients/{clinic.PatientId}/attachments";
        var justOver = new byte[20 * 1024 * 1024 + 1];
        Png.CopyTo(justOver, 0);
        var wayOver = new byte[21 * 1024 * 1024];
        Png.CopyTo(wayOver, 0);

        var over = await clinic.Nurse.PostAsync(url, Form(justOver));
        var beyondTheFormLimit = await clinic.Nurse.PostAsync(url, Form(wayOver));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Equal(ErrorCodes.PayloadTooLarge, (await ErrorOf(over)).Code);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, beyondTheFormLimit.StatusCode);

        var exactly = new byte[20 * 1024 * 1024];
        Png.CopyTo(exactly, 0);
        Assert.Equal(HttpStatusCode.Created, (await clinic.Nurse.PostAsync(url, Form(exactly))).StatusCode);
    }

    // ---------- Owners and permissions ----------

    [Fact]
    public async Task ConsultationAndClientFiles_FollowTheirOwners()
    {
        var clinic = await ClinicAsync();
        var consultation = await OpenConsultationAsync(clinic);

        var forConsultation = await OkAsync(await clinic.Vet.PostAsync($"/api/consultations/{consultation}/attachments", Form(Pdf, "lab.pdf")), HttpStatusCode.Created);
        Assert.Equal("Consultation", forConsultation.GetProperty("ownerType").GetString());
        Assert.Equal(clinic.PatientId, forConsultation.GetProperty("patientId").GetGuid());
        Assert.Equal(consultation, forConsultation.GetProperty("consultationId").GetGuid());

        // After sign-off a late lab report can still be attached; after a discard nothing can.
        await OkAsync(await clinic.Vet.PostAsync($"/api/consultations/{consultation}/complete", null));
        await UploadAsync(clinic.Vet, $"/api/consultations/{consultation}", Pdf);
        var discarded = await OpenConsultationAsync(clinic);
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{discarded}/discard", new { reason = "Mistake" }));
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Vet.PostAsync($"/api/consultations/{discarded}/attachments", Form(Pdf))).StatusCode);

        var forClient = await OkAsync(await clinic.Desk.PostAsync($"/api/clients/{clinic.ClientId}/attachments", Form(Pdf, "consent.pdf")), HttpStatusCode.Created);
        Assert.Equal("Client", forClient.GetProperty("ownerType").GetString());
        Assert.Equal(JsonValueKind.Null, forClient.GetProperty("patientId").ValueKind);
        var clientFile = forClient.GetProperty("id").GetGuid();
        var patientFile = await UploadAsync(clinic.Nurse, $"/api/patients/{clinic.PatientId}");

        // Reception: client files yes, clinical files no.
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.PostAsync($"/api/patients/{clinic.PatientId}/attachments", Form(Png))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.GetAsync($"/api/attachments/{patientFile}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.GetAsync($"/api/attachments/{patientFile}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.GetAsync($"/api/patients/{clinic.PatientId}/attachments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clinic.Desk.GetAsync($"/api/attachments/{clientFile}/content")).StatusCode);

        // Nurses read client files (clients.read) but cannot add or withdraw them (no clients.manage).
        Assert.Equal(HttpStatusCode.OK, (await clinic.Nurse.GetAsync($"/api/attachments/{clientFile}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Nurse.PostAsync($"/api/clients/{clinic.ClientId}/attachments", Form(Pdf))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Nurse.PostAsJsonAsync($"/api/attachments/{clientFile}/withdraw", new { reason = "x" })).StatusCode);

        // Administrators read everything and manage client files, but write no clinical records.
        Assert.Equal(HttpStatusCode.OK, (await clinic.Admin.GetAsync($"/api/attachments/{patientFile}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Admin.PostAsync($"/api/patients/{clinic.PatientId}/attachments", Form(Png))).StatusCode);

        var inventory = await factory.SignInAsync(await factory.CreateColleagueAsync(clinic.AdminUser, roles: Role.Inventory));
        Assert.Equal(HttpStatusCode.Forbidden, (await inventory.GetAsync($"/api/attachments/{clientFile}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inventory.GetAsync($"/api/clients/{clinic.ClientId}/attachments")).StatusCode);
    }

    [Fact]
    public async Task OtherClinicsOwnersAndFiles_AreNotFound()
    {
        var clinic = await ClinicAsync();
        var other = await ClinicAsync();
        var theirConsultation = await OpenConsultationAsync(other);
        var theirs = await UploadAsync(other.Nurse, $"/api/patients/{other.PatientId}");

        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Nurse.PostAsync($"/api/patients/{other.PatientId}/attachments", Form(Png))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.PostAsync($"/api/consultations/{theirConsultation}/attachments", Form(Png))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.PostAsync($"/api/clients/{other.ClientId}/attachments", Form(Png))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/attachments/{theirs}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/attachments/{theirs}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.PostAsJsonAsync($"/api/attachments/{theirs}/withdraw", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/patients/{other.PatientId}/attachments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Desk.GetAsync($"/api/clients/{other.ClientId}/attachments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clinic.Vet.GetAsync($"/api/attachments/{Guid.NewGuid()}")).StatusCode);
    }

    // ---------- Lists and withdrawal ----------

    [Fact]
    public async Task Withdrawing_HidesTheFile_ButKeepsTheRecordAndTheContent()
    {
        var clinic = await ClinicAsync();
        var consultation = await OpenConsultationAsync(clinic);
        var patientFile = await UploadAsync(clinic.Nurse, $"/api/patients/{clinic.PatientId}");
        var consultationFile = await UploadAsync(clinic.Vet, $"/api/consultations/{consultation}", Pdf);
        var clientFile = await UploadAsync(clinic.Desk, $"/api/clients/{clinic.ClientId}", Pdf);
        var filesBefore = Directory.GetFiles(factory.FileStorageRoot, "*", SearchOption.AllDirectories).Length;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Nurse.PostAsJsonAsync($"/api/attachments/{patientFile}/withdraw", new { reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Desk.PostAsJsonAsync($"/api/attachments/{patientFile}/withdraw", new { reason = "x" })).StatusCode);
        var withdrawn = await OkAsync(await clinic.Nurse.PostAsJsonAsync($"/api/attachments/{patientFile}/withdraw", new { reason = " Wrong patient " }));
        await OkAsync(await clinic.Desk.PostAsJsonAsync($"/api/attachments/{clientFile}/withdraw", new { reason = "Duplicate" }));

        Assert.Equal("Withdrawn", withdrawn.GetProperty("status").GetString());
        Assert.Equal("Wrong patient", withdrawn.GetProperty("withdrawReason").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Vet.GetAsync($"/api/attachments/{patientFile}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Nurse.PostAsJsonAsync($"/api/attachments/{patientFile}/withdraw", new { reason = "again" })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await clinic.Nurse.DeleteAsync($"/api/attachments/{patientFile}")).StatusCode);
        Assert.Equal(filesBefore, Directory.GetFiles(factory.FileStorageRoot, "*", SearchOption.AllDirectories).Length);
        Assert.DoesNotContain("Wrong patient", (await factory.AuditEntriesAsync(patientFile)).Single(e => e.Action == AuditActions.AttachmentWithdrawn).Metadata);

        var patientList = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/attachments");
        Assert.Equal([consultationFile], patientList.GetProperty("data").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()));
        var withdrawnList = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/attachments?status=Withdrawn");
        Assert.Equal([patientFile], withdrawnList.GetProperty("data").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()));
        var consultationList = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/consultations/{consultation}/attachments");
        Assert.Equal(1, consultationList.GetProperty("pagination").GetProperty("total").GetInt32());
        var clientList = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/clients/{clinic.ClientId}/attachments");
        Assert.Equal(0, clientList.GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.Contains(AuditActions.AttachmentsListed, (await factory.AuditEntriesAsync(clinic.PatientId)).Select(e => e.Action));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clinic.Vet.GetAsync($"/api/patients/{clinic.PatientId}/attachments?status=withdrawn")).StatusCode);
    }

    [Fact]
    public async Task History_ShowsPatientAndConsultationFiles_ToClinicalReadersOnly()
    {
        var clinic = await ClinicAsync();
        var consultation = await OpenConsultationAsync(clinic);
        var patientFile = await UploadAsync(clinic.Nurse, $"/api/patients/{clinic.PatientId}");
        var consultationFile = await UploadAsync(clinic.Vet, $"/api/consultations/{consultation}", Pdf);
        var withdrawnFile = await UploadAsync(clinic.Nurse, $"/api/patients/{clinic.PatientId}");
        await OkAsync(await clinic.Nurse.PostAsJsonAsync($"/api/attachments/{withdrawnFile}/withdraw", new { reason = "x" }));
        await UploadAsync(clinic.Desk, $"/api/clients/{clinic.ClientId}", Pdf);

        var forVet = await clinic.Vet.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");
        var forDesk = await clinic.Desk.GetFromJsonAsync<JsonElement>($"/api/patients/{clinic.PatientId}/history");

        var entries = forVet.GetProperty("data").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "AttachmentAdded").ToList();
        Assert.Equal(new[] { patientFile, consultationFile }.Order(), entries.Select(e => e.GetProperty("id").GetGuid()).Order());
        var details = entries.Single(e => e.GetProperty("id").GetGuid() == consultationFile).GetProperty("details").GetProperty("attachment");
        Assert.Equal(["id", "fileName", "contentType", "sizeBytes", "description", "consultationId"], details.EnumerateObject().Select(p => p.Name));
        Assert.Equal(consultation, details.GetProperty("consultationId").GetGuid());
        Assert.DoesNotContain(forDesk.GetProperty("data").EnumerateArray(), e => e.GetProperty("type").GetString() == "AttachmentAdded");
    }

    // ---------- Database rules ----------

    [Theory]
    [InlineData("UPDATE attachments SET file_name = 'renamed.png' WHERE id = {0}")]
    [InlineData("UPDATE attachments SET patient_id = NULL, client_id = (SELECT client_id FROM patients WHERE id = patient_id) WHERE id = {0}")]
    [InlineData("DELETE FROM attachments WHERE id = {0}")]
    [InlineData("UPDATE attachments SET status = 'Withdrawn', withdrawn_at = now(), withdrawn_by_user_id = uploaded_by_user_id, withdraw_reason = 'x', storage_key = repeat('a', 32) WHERE id = {0}")]
    [InlineData("TRUNCATE attachments")]
    public async Task Database_NeverChangesAnAttachment_ExceptToWithdrawIt(string sql)
    {
        var clinic = await ClinicAsync();
        var id = await UploadAsync(clinic.Nurse, $"/api/patients/{clinic.PatientId}");

        var exception = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, id)));
        Assert.Equal("42501", exception.SqlState);

        await factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE attachments SET status = 'Withdrawn', withdrawn_at = now(), withdrawn_by_user_id = uploaded_by_user_id, withdraw_reason = 'x' WHERE id = {0}", id));
        var undo = await Assert.ThrowsAsync<PostgresException>(() => factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE attachments SET status = 'Active', withdrawn_at = NULL, withdrawn_by_user_id = NULL, withdraw_reason = NULL WHERE id = {0}", id)));
        Assert.Equal("42501", undo.SqlState);
    }

    [Fact]
    public async Task Database_RefusesWithdrawnInserts_DiscardedLinks_AndTwoOwners()
    {
        var clinic = await ClinicAsync();
        var consultation = await OpenConsultationAsync(clinic);
        await OkAsync(await clinic.Vet.PostAsJsonAsync($"/api/consultations/{consultation}/discard", new { reason = "Mistake" }));
        const string prefix =
            "INSERT INTO attachments (id, clinic_id, storage_key, file_name, content_type, size_bytes, sha256, created_at, updated_at, " +
            "patient_id, consultation_id, client_id, status, withdrawn_at, withdrawn_by_user_id, withdraw_reason) " +
            "SELECT gen_random_uuid(), c.clinic_id, md5(random()::text), 'f.png', 'image/png', 1, repeat('0', 64), now(), now(), ";
        const string suffix = " FROM consultations c JOIN patients p ON p.id = c.patient_id WHERE c.id = {0}";
        const string withdrawnInsert = prefix + "c.patient_id, NULL, NULL, 'Withdrawn', now(), c.veterinarian_id, 'x'" + suffix;
        const string discardedLink = prefix + "c.patient_id, c.id, NULL, 'Active', NULL, NULL, NULL" + suffix;
        const string twoOwners = prefix + "c.patient_id, NULL, p.client_id, 'Active', NULL, NULL, NULL" + suffix;

        async Task<string> Fails(string sql)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                factory.UpdateDatabaseAsync(db => db.Database.ExecuteSqlRawAsync(sql, consultation)));
            return exception.SqlState;
        }

        Assert.Equal("42501", await Fails(withdrawnInsert));
        Assert.Equal("42501", await Fails(discardedLink));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await Fails(twoOwners));
    }
}
