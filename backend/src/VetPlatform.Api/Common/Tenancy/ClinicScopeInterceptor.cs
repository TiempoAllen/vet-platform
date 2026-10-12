using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace VetPlatform.Api.Common.Tenancy;

/// <summary>
/// Write-side tenant guard (the read side is the query filter in <c>AppDbContext</c>). When a request acts for a
/// clinic, new clinic-owned rows default to that clinic, and saving a row that belongs to (or is moved to) another
/// clinic throws. Without a clinic (CLI, background work), the clinic must be set explicitly.
/// </summary>
public sealed class ClinicScopeInterceptor(ITenantContext tenant) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Check(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var clinicId = tenant.ClinicId;
        foreach (var entry in context.ChangeTracker.Entries<IClinicOwned>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (entry.State == EntityState.Added && entry.Entity.ClinicId == Guid.Empty)
            {
                entry.Entity.ClinicId = clinicId
                    ?? throw new InvalidOperationException(
                        $"{entry.Entity.GetType().Name} has no clinic and there is no current clinic to default to.");
            }

            var original = entry.State == EntityState.Added
                ? entry.Entity.ClinicId
                : entry.Property(nameof(IClinicOwned.ClinicId)).OriginalValue as Guid? ?? Guid.Empty;

            if (clinicId is { } current && (entry.Entity.ClinicId != current || original != current))
            {
                throw new InvalidOperationException(
                    $"Refusing to save a {entry.Entity.GetType().Name} outside the current clinic.");
            }

            if (entry.State == EntityState.Modified && entry.Entity.ClinicId != original)
            {
                throw new InvalidOperationException(
                    $"Refusing to move a {entry.Entity.GetType().Name} to another clinic.");
            }
        }
    }
}
