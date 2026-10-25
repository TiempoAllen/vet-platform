using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Persistence;

namespace VetPlatform.Api.Features.Consultations;

/// <summary>
/// The shared rule for linking a record to a consultation (vaccinations follow the same rule): an in-progress or
/// completed consultation of the same patient. Must run inside the caller's transaction: the consultation row is locked
/// <c>FOR SHARE</c> after its clinic-filtered lookup, so a concurrent discard cannot slip in before the insert.
/// </summary>
public static class ConsultationLinks
{
    public const string Message = "The consultation was not found, is discarded, or is for another patient.";

    public static async Task<bool> IsLinkableAsync(AppDbContext db, Guid consultationId, Guid patientId, CancellationToken cancellationToken)
    {
        if (!await db.Consultations.AsNoTracking().AnyAsync(c => c.Id == consultationId && c.PatientId == patientId, cancellationToken))
        {
            return false;
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM consultations WHERE id = {consultationId} FOR SHARE", cancellationToken);
        return await db.Consultations.AsNoTracking().AnyAsync(
            c => c.Id == consultationId && c.Status != ConsultationStatus.Discarded, cancellationToken);
    }
}
