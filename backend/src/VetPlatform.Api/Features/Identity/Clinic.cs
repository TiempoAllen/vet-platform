using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;

namespace VetPlatform.Api.Features.Identity;

/// <summary>
/// The tenant: a veterinary practice (docs/DATABASE.md section 2). B-03 holds only what sign-in needs; contact
/// details, timezone, currency, tax and settings are added by the clinic settings milestone (B-26).
/// </summary>
public sealed class Clinic : Entity
{
    public const int NameMaxLength = 200;

    public required string Name { get; set; }

    public ClinicStatus Status { get; set; } = ClinicStatus.Active;
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
        builder.ToTable("clinics");
        builder.Property(clinic => clinic.Name).HasMaxLength(Clinic.NameMaxLength);
        builder.Property(clinic => clinic.Status).HasConversion<string>().HasMaxLength(32);
    }
}
