using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Features.Auth;

namespace VetPlatform.Api.Common.Auditing;

/// <summary>
/// An append-only record of a security or business event (docs/DATABASE.md section 10, SECURITY.md section 11).
/// A database trigger rejects UPDATE and DELETE. Metadata must never contain secrets, passwords, tokens or more
/// personal data than the event needs.
/// </summary>
public sealed class AuditLog
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Null only for events with no clinic, such as a sign-in attempt for an unknown email.</summary>
    public Guid? ClinicId { get; init; }

    public Guid? ActorUserId { get; init; }

    public required string Action { get; init; }

    public string? EntityType { get; init; }

    public Guid? EntityId { get; init; }

    /// <summary>JSON object (jsonb).</summary>
    public string? Metadata { get; init; }

    public string? RequestId { get; init; }
}

/// <summary>Stable action names. Clients and reports may filter on them; do not rename.</summary>
public static class AuditActions
{
    public const string LoginSucceeded = "auth.login_succeeded";
    public const string LoginFailed = "auth.login_failed";
    public const string AccountLocked = "auth.account_locked";
    public const string Logout = "auth.logout";
    public const string PasswordChanged = "auth.password_changed";
    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserRolesChanged = "user.roles_changed";
}

/// <summary>
/// Adds audit entries to the current unit of work; they are written by the caller's next <c>SaveChanges</c>, so an
/// entry commits atomically with the change it describes. Actor, clinic and request id default to the current request.
/// </summary>
public sealed class AuditTrail(Persistence.AppDbContext db, IHttpContextAccessor httpContextAccessor, TimeProvider timeProvider)
{
    public void Record(
        string action,
        string? entityType = null,
        Guid? entityId = null,
        object? metadata = null,
        Guid? actorUserId = null,
        Guid? clinicId = null)
    {
        var context = httpContextAccessor.HttpContext;
        var principal = context?.User;
        var signedIn = principal?.Identity?.IsAuthenticated == true;

        db.AuditLogs.Add(new AuditLog
        {
            OccurredAt = timeProvider.GetUtcNow(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            ActorUserId = actorUserId ?? (signedIn ? AuthClaims.GetUserId(principal!) : null),
            ClinicId = clinicId ?? (signedIn ? AuthClaims.GetClinicId(principal!) : null),
            Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata, JsonSerializerOptions.Web),
            RequestId = context?.TraceIdentifier,
        });
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.Property(log => log.Action).HasMaxLength(100);
        builder.Property(log => log.EntityType).HasMaxLength(100);
        builder.Property(log => log.Metadata).HasColumnType("jsonb");
        builder.Property(log => log.RequestId).HasMaxLength(64);

        // Matches the expected B-27 queries: a clinic's log newest first, and the history of one entity.
        builder.HasIndex(log => new { log.ClinicId, log.OccurredAt });
        builder.HasIndex(log => new { log.EntityType, log.EntityId });
    }
}
