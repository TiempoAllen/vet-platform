using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;

namespace VetPlatform.Api.Features.Identity;

/// <summary>
/// A staff member who can sign in (docs/DATABASE.md section 3). Belongs to exactly one clinic. Email is unique
/// across the platform, case-insensitively, so it alone identifies the account at sign-in (ADR-003). Roles arrive
/// in B-04.
/// </summary>
public sealed class User : Entity, IClinicOwned
{
    public const int NameMaxLength = 200;
    public const int EmailMaxLength = 254;

    public Guid ClinicId { get; set; }

    public Clinic? Clinic { get; set; }

    public required string Name { get; set; }

    /// <summary>Always stored normalised; set it through <see cref="NormalizeEmail"/>.</summary>
    public required string Email { get; set; }

    public string? Phone { get; set; }

    /// <summary>ASP.NET Core Identity <c>PasswordHasher</c> output. Never the password itself; never logged or returned.</summary>
    public required string PasswordHash { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Active;

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Consecutive failed sign-ins since the last success; drives temporary lockout.</summary>
    public int FailedLoginCount { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public List<UserRole> Roles { get; set; } = [];

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

public enum UserStatus
{
    Active,
    Disabled,
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", table =>
        {
            table.HasCheckConstraint("ck_users_status", "status IN ('Active', 'Disabled')");
            table.HasCheckConstraint("ck_users_failed_login_count", "failed_login_count >= 0");
        });
        builder.Property(user => user.Name).HasMaxLength(User.NameMaxLength);
        // Case-insensitive collation: the unique index rejects "Jo@x.com" next to "jo@x.com" even if a writer skips
        // NormalizeEmail, and sign-in lookups match regardless of case.
        builder.Property(user => user.Email)
            .HasMaxLength(User.EmailMaxLength)
            .UseCollation(AppDbContext.CaseInsensitiveCollation);
        builder.Property(user => user.Phone).HasMaxLength(32);
        builder.Property(user => user.PasswordHash).HasMaxLength(512);
        builder.Property(user => user.Status).HasConversion<string>().HasMaxLength(32);

        builder.HasIndex(user => user.Email).IsUnique();
        builder.HasIndex(user => user.ClinicId);

        builder.HasMany(user => user.Roles)
            .WithOne()
            .HasForeignKey(userRole => userRole.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(user => user.Clinic)
            .WithMany()
            .HasForeignKey(user => user.ClinicId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
