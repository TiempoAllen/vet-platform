using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Appointments;
using VetPlatform.Api.Features.Consultations;

namespace VetPlatform.Api.Features.Patients;

/// <summary>
/// Kinds of patient history entry (docs/API.md section 6.1). Stored nowhere: entries are read from their source
/// records. Later milestones add kinds (consultations, vaccinations, invoices, ...); clients must ignore kinds they do
/// not know. Names are part of the contract: never rename one.
/// </summary>
public enum PatientHistoryEntryType
{
    PatientRegistered,
    AppointmentCompleted,
    AppointmentCancelled,
    AppointmentNoShow,

    /// <summary>Clinical: only for callers with <c>clinical.read</c>, and reading it is audited.</summary>
    ConsultationCompleted,
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

/// <summary>The appointment an appointment entry is about.</summary>
public sealed record PatientHistoryAppointment(
    Guid Id,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    AppointmentType Type,
    AppointmentPersonSummary Veterinarian);

/// <summary>Details of <c>AppointmentCompleted</c> and <c>AppointmentNoShow</c>.</summary>
public sealed record PatientAppointmentDetails(PatientHistoryAppointment Appointment);

/// <summary>Details of <c>AppointmentCancelled</c>. The cancellation reason is deliberately not shown here.</summary>
public sealed record PatientAppointmentCancelledDetails(PatientHistoryAppointment Appointment, AppointmentCancelledBy CancelledBy);

/// <summary>The consultation a <c>ConsultationCompleted</c> entry is about; <c>assessment</c> is the clinical summary.</summary>
public sealed record PatientHistoryConsultation(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    Guid? AppointmentId,
    AppointmentPersonSummary Veterinarian,
    string? Assessment,
    IReadOnlyList<PatientHistoryDiagnosis> Diagnoses,
    IReadOnlyList<PatientHistoryPrescription> Prescriptions);

/// <summary>A diagnosis as summarised in the history (primary first).</summary>
public sealed record PatientHistoryDiagnosis(Guid Id, string Name, DiagnosisStatus Status, bool IsPrimary);

/// <summary>A prescription as summarised in the history (in the order recorded), with its current status.</summary>
public sealed record PatientHistoryPrescription(Guid Id, string MedicationName, PrescriptionStatus Status);

/// <summary>Details of <c>ConsultationCompleted</c>.</summary>
public sealed record PatientConsultationDetails(PatientHistoryConsultation Consultation);

/// <summary>
/// The patient history read model (B-07, ADR-007; appointment entries B-10, ADR-010): one newest-first timeline over
/// every source of patient events. Each source is projected to (type, id, occurred_at); the sources are combined with
/// UNION ALL, ordered and paged in the database, and only then are the page's rows loaded per type.
/// </summary>
/// <remarks>
/// Each source decides its own permission: entries a caller may not see (later, clinical ones without
/// <c>clinical.read</c>) are left out of the union, so <c>total</c> and paging never reveal them.
/// </remarks>
public sealed class PatientHistoryService(AppDbContext db, ITenantContext tenant, AuditTrail audit)
{
    /// <summary>The row every source is projected to before the union.</summary>
    private sealed class HistoryKey
    {
        public required string Type { get; init; }

        public required Guid Id { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }
    }

    /// <param name="includeClinical">
    /// Whether the caller holds <c>clinical.read</c>. Without it clinical sources are left out of the union, so they
    /// are neither shown nor counted. With it, a page that contains clinical entries is an audited read.
    /// </param>
    public async Task<PagedResponse<PatientHistoryEntry>> ListAsync(
        Guid patientId,
        PageQuery page,
        bool includeClinical,
        CancellationToken cancellationToken)
    {
        // The patient query is clinic-filtered, so another clinic's patient is 404 like everywhere else.
        if (!await db.Patients.AnyAsync(patient => patient.Id == patientId, cancellationToken))
        {
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The patient was not found.");
        }

        const string registered = nameof(PatientHistoryEntryType.PatientRegistered);
        const string completed = nameof(PatientHistoryEntryType.AppointmentCompleted);
        const string cancelled = nameof(PatientHistoryEntryType.AppointmentCancelled);
        const string noShow = nameof(PatientHistoryEntryType.AppointmentNoShow);
        const string consultationCompleted = nameof(PatientHistoryEntryType.ConsultationCompleted);

        var registrations = db.ForCurrentClinic(tenant)
            .Where(entry => entry.EntityType == nameof(Patient)
                && entry.EntityId == patientId
                && entry.Action == AuditActions.PatientCreated)
            .Select(entry => new HistoryKey { Type = registered, Id = entry.Id, OccurredAt = entry.OccurredAt });

        // Appointments are clinic-filtered; only finished ones belong in a history.
        var appointments = db.Appointments
            .Where(a => a.PatientId == patientId
                && (a.Status == AppointmentStatus.Completed
                    || a.Status == AppointmentStatus.Cancelled
                    || a.Status == AppointmentStatus.NoShow))
            .Select(a => new HistoryKey
            {
                Type = a.Status == AppointmentStatus.Completed ? completed
                    : a.Status == AppointmentStatus.Cancelled ? cancelled
                    : noShow,
                Id = a.Id,
                OccurredAt = a.Status == AppointmentStatus.Completed ? a.CompletedAt!.Value
                    : a.Status == AppointmentStatus.Cancelled ? a.CancelledAt!.Value
                    : a.NoShowAt!.Value,
            });

        var sources = registrations.Concat(appointments);
        if (includeClinical)
        {
            // Completed consultations only: in-progress notes are not yet a record, discarded ones never were.
            sources = sources.Concat(db.Consultations
                .Where(c => c.PatientId == patientId && c.Status == ConsultationStatus.Completed)
                .Select(c => new HistoryKey { Type = consultationCompleted, Id = c.Id, OccurredAt = c.CompletedAt!.Value }));
        }

        // Documented order: occurredAt, then id, then type, all descending.
        var keys = await sources
            .OrderByDescending(key => key.OccurredAt)
            .ThenByDescending(key => key.Id)
            .ThenByDescending(key => key.Type)
            .ToPageAsync(page, key => key, cancellationToken);

        var auditIds = keys.Data.Where(k => k.Type == registered).Select(k => k.Id).ToList();
        var appointmentIds = keys.Data.Where(k => k.Type is completed or cancelled or noShow).Select(k => k.Id).ToList();
        var consultationIds = keys.Data.Where(k => k.Type == consultationCompleted).Select(k => k.Id).ToList();
        var auditEntries = auditIds.Count == 0
            ? []
            : await db.ForCurrentClinic(tenant).AsNoTracking()
                .Where(entry => auditIds.Contains(entry.Id))
                .ToDictionaryAsync(entry => entry.Id, cancellationToken);
        var appointmentRows = appointmentIds.Count == 0
            ? []
            : await db.Appointments.AsNoTracking()
                .Include(a => a.Veterinarian)
                .Where(a => appointmentIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, cancellationToken);

        var consultationRows = consultationIds.Count == 0
            ? []
            : await db.Consultations.AsNoTracking()
                .Include(c => c.Veterinarian)
                .Include(c => c.Diagnoses)
                .Include(c => c.Prescriptions)
                .AsSplitQuery()
                .Where(c => consultationIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, cancellationToken);

        var actors = await ActorsAsync(
            auditEntries.Values.Select(entry => entry.ActorUserId)
                .Concat(appointmentRows.Values.Select(a => a.StatusChangedByUserId))
                .Concat(consultationRows.Values.Select(c => c.CompletedByUserId)),
            cancellationToken);
        var clients = await ClientsAsync(auditEntries.Values, cancellationToken);

        PatientHistoryActor? Actor(Guid? id) => id is { } actorId ? actors.GetValueOrDefault(actorId) : null;

        var entries = keys.Data.Select(key =>
        {
            var type = Enum.Parse<PatientHistoryEntryType>(key.Type);
            if (type == PatientHistoryEntryType.PatientRegistered)
            {
                var entry = auditEntries[key.Id];
                return new PatientHistoryEntry(
                    key.Id, type, key.OccurredAt, Actor(entry.ActorUserId),
                    new PatientRegisteredDetails(ClientIdOf(entry) is { } clientId ? clients.GetValueOrDefault(clientId) : null));
            }

            if (type == PatientHistoryEntryType.ConsultationCompleted)
            {
                var consultation = consultationRows[key.Id];
                return new PatientHistoryEntry(
                    key.Id, type, key.OccurredAt, Actor(consultation.CompletedByUserId),
                    new PatientConsultationDetails(new PatientHistoryConsultation(
                        consultation.Id, consultation.StartedAt, consultation.CompletedAt!.Value, consultation.AppointmentId,
                        new AppointmentPersonSummary(consultation.Veterinarian!.Id, consultation.Veterinarian.Name),
                        consultation.Assessment,
                        consultation.Diagnoses
                            .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.CreatedAt).ThenBy(d => d.Id)
                            .Select(d => new PatientHistoryDiagnosis(d.Id, d.Name, d.Status, d.IsPrimary))
                            .ToList(),
                        consultation.Prescriptions
                            .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                            .Select(p => new PatientHistoryPrescription(p.Id, p.MedicationName, p.Status))
                            .ToList())));
            }

            var appointment = appointmentRows[key.Id];
            var summary = new PatientHistoryAppointment(
                appointment.Id, appointment.StartAt, appointment.EndAt, appointment.Type,
                new AppointmentPersonSummary(appointment.Veterinarian!.Id, appointment.Veterinarian.Name));
            object details = type == PatientHistoryEntryType.AppointmentCancelled
                ? new PatientAppointmentCancelledDetails(summary, appointment.CancelledBy!.Value)
                : new PatientAppointmentDetails(summary);
            return new PatientHistoryEntry(key.Id, type, key.OccurredAt, Actor(appointment.StatusChangedByUserId), details);
        }).ToList();

        // Reading clinical entries is a clinical read (SECURITY.md section 10): record which ones were shown.
        if (consultationIds.Count > 0)
        {
            audit.Record(AuditActions.PatientHistoryViewed, nameof(Patient), patientId, new { consultationIds });
            await db.SaveChangesAsync(cancellationToken);
        }

        return new PagedResponse<PatientHistoryEntry>(entries, keys.Pagination);
    }

    /// <summary>Actors are looked up through the clinic-filtered users, so only this clinic's staff are named.</summary>
    private async Task<Dictionary<Guid, PatientHistoryActor>> ActorsAsync(
        IEnumerable<Guid?> actorIds,
        CancellationToken cancellationToken)
    {
        var ids = actorIds.OfType<Guid>().Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Users.AsNoTracking()
                .Where(user => ids.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => new PatientHistoryActor(user.Id, user.Name), cancellationToken);
    }

    /// <summary>Owners are resolved at read time through the clinic-filtered clients (current names).</summary>
    private async Task<Dictionary<Guid, PatientClientSummary>> ClientsAsync(
        IEnumerable<AuditLog> entries,
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
