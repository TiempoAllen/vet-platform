using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;

namespace VetPlatform.Api.Features.Identity;

/// <summary>
/// A server-side sign-in session (ADR-003). The session cookie only carries this id; the row decides whether the
/// cookie is still valid, so logout and expiry take effect immediately. Kept after revocation for auditability.
/// </summary>
public sealed class UserSession : Entity
{
    public Guid UserId { get; set; }

    public User? User { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Absolute end of the session, however active it is.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");
        builder.HasIndex(session => session.UserId);

        builder.HasOne(session => session.User)
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
