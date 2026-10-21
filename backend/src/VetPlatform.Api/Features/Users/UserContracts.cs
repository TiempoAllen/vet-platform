using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Users;

/// <summary>A staff member as returned by the users API (docs/API.md section 4). Never includes credentials or lockout state.</summary>
public sealed record UserResponse(
    Guid Id,
    string Name,
    string Email,
    string? Phone,
    UserStatus Status,
    IReadOnlyList<Role> Roles,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static UserResponse From(User user) => new(
        user.Id, user.Name, user.Email, user.Phone, user.Status,
        user.Roles.Select(role => role.Role).Order().ToList(),
        user.LastLoginAt, user.CreatedAt, user.UpdatedAt);
}

public sealed class UserListQuery : PageQuery, IValidatableObject
{
    public static readonly string[] SortFields = ["name", "email", "createdAt", "lastLoginAt"];

    /// <summary>Matches name or email, case-insensitively, anywhere in the value.</summary>
    [MaxLength(100)]
    public string? Q { get; init; }

    /// <summary><c>Active</c> or <c>Disabled</c>. A string so only exact names are accepted (see <see cref="StrictEnum"/>).</summary>
    public string? Status { get; init; }

    /// <summary>A role name. A string so only exact names are accepted (see <see cref="StrictEnum"/>).</summary>
    public string? Role { get; init; }

    public UserStatus? ParsedStatus => StrictEnum.TryParse<UserStatus>(Status, out var status) ? status : null;

    public Common.Authorization.Role? ParsedRole =>
        StrictEnum.TryParse<Common.Authorization.Role>(Role, out var role) ? role : null;

    /// <summary><c>name</c> (default), <c>email</c>, <c>createdAt</c> or <c>lastLoginAt</c>; prefix with <c>-</c> for descending.</summary>
    public string Sort { get; init; } = "name";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<UserStatus>()}.", [nameof(Status)]);
        }

        if (Role is not null && ParsedRole is null)
        {
            yield return new ValidationResult(
                $"Role must be one of {StrictEnum.Names<Common.Authorization.Role>()}.", [nameof(Role)]);
        }

        if (!SortFields.Contains(SortSpec.Parse(Sort).Field, StringComparer.Ordinal))
        {
            yield return new ValidationResult(
                $"Sort must be one of {string.Join(", ", SortFields)}, optionally prefixed with '-'.", [nameof(Sort)]);
        }
    }
}

public sealed record CreateUserRequest(
    [Required, MaxLength(User.NameMaxLength)] string? Name,
    [Required, EmailAddress, MaxLength(User.EmailMaxLength)] string? Email,
    [MaxLength(32)] string? Phone,
    [Required, MaxLength(PasswordPolicy.MaxLength)] string? Password,
    [Required, MinLength(1)] IReadOnlyList<Role>? Roles);

/// <summary>Partial update: omitted (null) fields are left unchanged; an empty <c>phone</c> clears it.</summary>
public sealed record UpdateUserRequest(
    [MaxLength(User.NameMaxLength)] string? Name,
    [EmailAddress, MaxLength(User.EmailMaxLength)] string? Email,
    [MaxLength(32)] string? Phone,
    UserStatus? Status);

public sealed record SetRolesRequest([Required, MinLength(1)] IReadOnlyList<Role>? Roles);
