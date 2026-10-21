using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;

namespace VetPlatform.Api.Common.Auditing;

/// <summary>
/// The one way to read audit entries for the current clinic. <see cref="AuditLog"/> is not <see cref="IClinicOwned"/>
/// (sign-in attempts for unknown emails have no clinic), so it has no query filter and every tenant-scoped read must
/// filter <c>clinic_id</c> explicitly (ADR-004). Fails closed: without a current clinic nothing matches.
/// </summary>
public static class AuditLogQueries
{
    public static IQueryable<AuditLog> ForCurrentClinic(this AppDbContext db, ITenantContext tenant)
    {
        var clinicId = tenant.ClinicId ?? Guid.Empty;
        return db.AuditLogs.Where(entry => entry.ClinicId == clinicId);
    }
}
