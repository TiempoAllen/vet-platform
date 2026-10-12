using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Authorization;

namespace VetPlatform.Api.Features.Identity;

/// <summary>
/// A role held by a user (docs/DATABASE.md UserRole). Roles themselves and their permissions are fixed in code
/// (<see cref="Permissions"/>, ADR-004), so there are no role or permission tables.
/// </summary>
public sealed class UserRole
{
    public Guid UserId { get; set; }

    public Role Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_roles", table => table.HasCheckConstraint(
            "ck_user_roles_role",
            "role IN ('Admin', 'Veterinarian', 'Nurse', 'Receptionist', 'Inventory')"));
        builder.HasKey(userRole => new { userRole.UserId, userRole.Role });
        builder.Property(userRole => userRole.Role).HasConversion<string>().HasMaxLength(32);
    }
}
