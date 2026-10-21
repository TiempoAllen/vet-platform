using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;

namespace VetPlatform.Api.Features.Identity;

/// <summary>
/// The tenant: a veterinary practice (docs/DATABASE.md section 2). Holds what sign-in and scheduling need; contact
/// details, currency, tax and other settings are added by the clinic settings milestone (B-26).
/// </summary>
public sealed class Clinic : Entity
{
    public const int NameMaxLength = 200;
    public const int TimeZoneMaxLength = 64;

    public required string Name { get; set; }

    public ClinicStatus Status { get; set; } = ClinicStatus.Active;

    /// <summary>
    /// IANA time zone (e.g. <c>Europe/London</c>) in which working hours and calendar dates are interpreted (B-08,
    /// ADR-008). Validate with <see cref="ClinicTimeZone.TryFind"/>.
    /// </summary>
    public string TimeZone { get; set; } = ClinicTimeZone.Default;
}

/// <summary>IANA time zone handling for clinics. Windows-style ids ("GMT Standard Time") are refused.</summary>
public static class ClinicTimeZone
{
    public const string Default = "UTC";

    public static bool TryFind(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id) || id.Length > Clinic.TimeZoneMaxLength)
        {
            return false;
        }

        if (id == Default)
        {
            return true;
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found) && found.HasIanaId)
        {
            zone = found;
            return true;
        }

        return false;
    }

    /// <summary>The zone of a stored clinic; falls back to UTC if the stored id is no longer known to this host.</summary>
    public static TimeZoneInfo Of(Clinic clinic) => TryFind(clinic.TimeZone, out var zone) ? zone : TimeZoneInfo.Utc;
}

public enum ClinicStatus
{
    Active,
    Suspended,
}

internal sealed class ClinicConfiguration : IEntityTypeConfiguration<Clinic>
{
    public void Configure(EntityTypeBuilder<Clinic> builder)
    {
        builder.ToTable("clinics", table =>
            table.HasCheckConstraint("ck_clinics_status", "status IN ('Active', 'Suspended')"));
        builder.Property(clinic => clinic.Name).HasMaxLength(Clinic.NameMaxLength);
        builder.Property(clinic => clinic.Status).HasConversion<string>().HasMaxLength(32);
        // Existing clinics get UTC; an administrator sets the real zone with PATCH /api/clinic.
        builder.Property(clinic => clinic.TimeZone)
            .HasMaxLength(Clinic.TimeZoneMaxLength)
            .HasDefaultValue(ClinicTimeZone.Default);
    }
}
