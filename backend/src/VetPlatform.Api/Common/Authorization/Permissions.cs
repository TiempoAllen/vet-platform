using System.Collections.Frozen;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace VetPlatform.Api.Common.Authorization;

/// <summary>
/// The fixed staff roles (docs/PRODUCT.md section 2; ADR-004). Stored by name, so never rename a member without a
/// data migration. A user may hold several roles; their permissions are the union.
/// </summary>
public enum Role
{
    Admin,
    Veterinarian,
    Nurse,
    Receptionist,
    Inventory,
}

/// <summary>
/// Permission catalogue. Each milestone adds the permissions its endpoints need and grants them below.
/// Values are stable strings: the frontend receives them from <c>GET /api/auth/me</c> to decide what to show.
/// </summary>
public static class Permissions
{
    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string ClientsRead = "clients.read";
    public const string ClientsManage = "clients.manage";
    public const string PatientsRead = "patients.read";
    public const string PatientsManage = "patients.manage";

    private static readonly FrozenDictionary<Role, FrozenSet<string>> Grants = new Dictionary<Role, FrozenSet<string>>
    {
        [Role.Admin] = new[] { UsersRead, UsersManage, ClientsRead, ClientsManage, PatientsRead, PatientsManage }.ToFrozenSet(),
        [Role.Veterinarian] = new[] { ClientsRead, PatientsRead, PatientsManage }.ToFrozenSet(),
        [Role.Nurse] = new[] { ClientsRead, PatientsRead, PatientsManage }.ToFrozenSet(),
        [Role.Receptionist] = new[] { ClientsRead, ClientsManage, PatientsRead, PatientsManage }.ToFrozenSet(),
        [Role.Inventory] = FrozenSet<string>.Empty,
    }.ToFrozenDictionary();

    public static IReadOnlyList<string> For(IEnumerable<Role> roles) =>
        roles.SelectMany(role => Grants[role]).Distinct().Order(StringComparer.Ordinal).ToList();

    public static bool Grant(IEnumerable<Role> roles, string permission) =>
        roles.Any(role => Grants[role].Contains(permission));

    /// <summary>Roles carried by a validated principal (added per request by session validation, never by the cookie).</summary>
    public static IEnumerable<Role> RolesOf(ClaimsPrincipal principal) =>
        principal.FindAll(ClaimTypes.Role)
            .Select(claim => Enum.TryParse<Role>(claim.Value, out var role) ? role : (Role?)null)
            .OfType<Role>();
}

/// <summary>Requires the signed-in user to hold a permission: <c>[RequirePermission(Permissions.UsersManage)]</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission) : AuthorizeAttribute, IAuthorizationRequirementData
{
    public string Permission { get; } = permission;

    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new PermissionRequirement(Permission)];
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (Permissions.Grant(Permissions.RolesOf(context.User), requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
