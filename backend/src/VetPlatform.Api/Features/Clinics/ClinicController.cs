using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Clinics;

/// <summary>The caller's clinic as returned by <c>GET /api/clinic</c> (docs/API.md section 4.1).</summary>
public sealed record ClinicResponse(Guid Id, string Name, string TimeZone)
{
    public static ClinicResponse From(Clinic clinic) => new(clinic.Id, clinic.Name, clinic.TimeZone);
}

/// <summary>Only the time zone for now; the other clinic settings arrive with B-26.</summary>
public sealed record UpdateClinicRequest([MaxLength(Clinic.TimeZoneMaxLength)] string? TimeZone);

/// <summary>
/// The caller's own clinic (B-08). <see cref="Clinic"/> is the tenant itself, not <see cref="IClinicOwned"/>, so it is
/// always loaded by the session's clinic id, never by an id from the request.
/// </summary>
[ApiController]
[Route("api/clinic")]
public sealed class ClinicController(AppDbContext db, ITenantContext tenant, AuditTrail audit) : ControllerBase
{
    /// <summary>Any signed-in user may read their own clinic's basic settings.</summary>
    [HttpGet]
    public async Task<ClinicResponse> Get(CancellationToken cancellationToken) =>
        ClinicResponse.From(await FindAsync(tracking: false, cancellationToken));

    [HttpPatch]
    [RequirePermission(Permissions.SettingsManage)]
    public async Task<ClinicResponse> Update(UpdateClinicRequest request, CancellationToken cancellationToken)
    {
        var clinic = await FindAsync(tracking: true, cancellationToken);
        if (request.TimeZone is { } timeZone && timeZone != clinic.TimeZone)
        {
            if (!ClinicTimeZone.TryFind(timeZone, out _))
            {
                throw new ApiException(
                    StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.",
                    [new ApiErrorDetail("timeZone", "The time zone must be an IANA id such as Europe/London.")]);
            }

            var before = clinic.TimeZone;
            clinic.TimeZone = timeZone;
            audit.Record(AuditActions.ClinicUpdated, nameof(Clinic), clinic.Id, new { fields = new[] { "timeZone" }, before, after = timeZone });
            await db.SaveChangesAsync(cancellationToken);
        }

        return ClinicResponse.From(clinic);
    }

    private async Task<Clinic> FindAsync(bool tracking, CancellationToken cancellationToken)
    {
        var clinicId = tenant.ClinicId ?? throw new InvalidOperationException("A signed-in request always has a clinic.");
        var clinics = tracking ? db.Clinics : db.Clinics.AsNoTracking();
        return await clinics.SingleAsync(clinic => clinic.Id == clinicId, cancellationToken);
    }
}
