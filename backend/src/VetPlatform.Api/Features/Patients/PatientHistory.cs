using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;

namespace VetPlatform.Api.Features.Patients;

/// <summary>
/// Kinds of patient history entry (docs/API.md section 6.1). Stored nowhere: entries are read from their source
/// records. Later milestones add kinds (consultations, vaccinations, invoices, ...); clients must ignore kinds they do
/// not know.
/// </summary>
public enum PatientHistoryEntryType
{
    PatientRegistered,
}

/// <summary>Who caused an entry; null when there was no signed-in user (the CLI) or the account cannot be shown.</summary>
public sealed record PatientHistoryActor(Guid Id, string Name);

/// <summary>
/// One timeline entry. <c>id</c> is the id of the source record and is unique per <c>type</c>; <c>details</c> has a
/// shape fixed per <c>type</c>.
/// </summary>
public sealed record PatientHistoryEntry(
    Guid Id,
    PatientHistoryEntryType Type,
    DateTimeOffset OccurredAt,
    PatientHistoryActor? Actor,
    object Details);

/// <summary>Details of <see cref="PatientHistoryEntryType.PatientRegistered"/>: the owner at registration, if still visible.</summary>
public sealed record PatientRegisteredDetails(PatientClientSummary? Client);

/// <summary>
/// The patient history read model (B-07, ADR-007): one newest-first timeline over every source of patient events.
/// Today the only source is the audit log's <c>patient.created</c> entries. New sources are added as further queries
/// projected to (type, id, occurred_at) and merged in SQL (UNION ALL) before paging, so paging stays in the database.
/// </summary>
/// <remarks>
/// Each source decides its own permission: entries a caller may not see (later, clinical ones without
/// <c>clinical.read</c>) are left out of the query, not hidden after paging.
/// </remarks>
public sealed class PatientHistoryService(AppDbContext db, ITenantContext tenant)
{
    public async Task<PagedResponse<PatientHistoryEntry>> ListAsync(
        Guid patientId,
        PageQuery page,
        CancellationToken cancellationToken)
    {
        // The patient query is clinic-filtered, so another clinic's patient is 404 like everywhere else.
        if (!await db.Patients.AnyAsync(patient => patient.Id == patientId, cancellationToken))
        {
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The patient was not found.");
        }

        var registrations = db.ForCurrentClinic(tenant).AsNoTracking()
            .Where(entry => entry.EntityType == nameof(Patient)
                && entry.EntityId == patientId
                && entry.Action == AuditActions.PatientCreated)
            // Documented order: occurredAt, then id, then type, all descending. With one source the type is
            // constant; the UNION over several sources (ADR-007) must sort on all three.
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id);

        var rows = await registrations.ToPageAsync(page, entry => entry, cancellationToken);
        var actors = await ActorsAsync(rows.Data, cancellationToken);
        var clients = await ClientsAsync(rows.Data, cancellationToken);

        return new PagedResponse<PatientHistoryEntry>(
            rows.Data.Select(entry => new PatientHistoryEntry(
                entry.Id,
                PatientHistoryEntryType.PatientRegistered,
                entry.OccurredAt,
                entry.ActorUserId is { } actorId ? actors.GetValueOrDefault(actorId) : null,
                new PatientRegisteredDetails(ClientIdOf(entry) is { } clientId ? clients.GetValueOrDefault(clientId) : null)))
                .ToList(),
            rows.Pagination);
    }

    /// <summary>Actors are looked up through the clinic-filtered users, so only this clinic's staff are named.</summary>
    private async Task<Dictionary<Guid, PatientHistoryActor>> ActorsAsync(
        IReadOnlyList<AuditLog> entries,
        CancellationToken cancellationToken)
    {
        var ids = entries.Select(entry => entry.ActorUserId).OfType<Guid>().Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Users.AsNoTracking()
                .Where(user => ids.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => new PatientHistoryActor(user.Id, user.Name), cancellationToken);
    }

    /// <summary>Owners are resolved at read time through the clinic-filtered clients (current names).</summary>
    private async Task<Dictionary<Guid, PatientClientSummary>> ClientsAsync(
        IReadOnlyList<AuditLog> entries,
        CancellationToken cancellationToken)
    {
        var ids = entries.Select(ClientIdOf).OfType<Guid>().Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Clients.AsNoTracking()
                .Where(client => ids.Contains(client.Id))
                .ToDictionaryAsync(
                    client => client.Id,
                    client => new PatientClientSummary(client.Id, client.FirstName, client.LastName),
                    cancellationToken);
    }

    /// <summary>The owner recorded by <c>patient.created</c> (<c>{"clientId": "..."}</c>), or null if absent or malformed.</summary>
    private static Guid? ClientIdOf(AuditLog entry)
    {
        if (entry.Metadata is null)
        {
            return null;
        }

        try
        {
            using var metadata = JsonDocument.Parse(entry.Metadata);
            return metadata.RootElement.ValueKind == JsonValueKind.Object
                && metadata.RootElement.TryGetProperty("clientId", out var clientId)
                && clientId.ValueKind == JsonValueKind.String
                && clientId.TryGetGuid(out var id)
                    ? id
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
