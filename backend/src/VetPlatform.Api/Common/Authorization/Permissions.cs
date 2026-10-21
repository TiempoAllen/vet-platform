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
    public const string SchedulesRead = "schedules.read";

    /// <summary>Change any veterinarian's working hours and exceptions, and clinic closures.</summary>
    public const string SchedulesManage = "schedules.manage";

    /// <summary>Change one's own working hours and exceptions (a veterinarian's own schedule).</summary>
    public const string SchedulesManageOwn = "schedules.manage_own";

    public const string SettingsManage = "settings.manage";

    private static readonly FrozenDictionary<Role, FrozenSet<string>> Grants = new Dictionary<Role, FrozenSet<string>>
    {
        [Role.Admin] = new[]
        {
            UsersRead, UsersManage, ClientsRead, ClientsManage, PatientsRead, PatientsManage, SchedulesRead,
            SchedulesManage, SettingsManage,
        }.ToFrozenSet(),
        [Role.Veterinarian] = new[] { ClientsRead, PatientsRead, PatientsManage, SchedulesRead, SchedulesManageOwn }.ToFrozenSet(),
        [Role.Nurse] = new[] { ClientsRead, PatientsRead, PatientsManage, SchedulesRead }.ToFrozenSet(),
        [Role.Receptionist] = new[] { ClientsRead, ClientsManage, PatientsRead, PatientsManage, SchedulesRead }.ToFrozenSet(),
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

/// <summary>
/// Requires at least one of several permissions: <c>[RequireAnyPermission(Permissions.SchedulesManage,
/// Permissions.SchedulesManageOwn)]</c>. For endpoints whose finer check (e.g. "only your own") happens in the
/// service; this keeps the 403 for callers with neither permission ahead of body validation.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireAnyPermissionAttribute(params string[] permissions) : AuthorizeAttribute, IAuthorizationRequirementData
{
    public IReadOnlyList<string> Permissions { get; } = permissions;

    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new AnyPermissionRequirement(Permissions)];
}

public sealed record AnyPermissionRequirement(IReadOnlyList<string> Permissions) : IAuthorizationRequirement;

public sealed class AnyPermissionHandler : AuthorizationHandler<AnyPermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AnyPermissionRequirement requirement)
    {
        var roles = VetPlatform.Api.Common.Authorization.Permissions.RolesOf(context.User).ToList();
        if (requirement.Permissions.Any(permission => VetPlatform.Api.Common.Authorization.Permissions.Grant(roles, permission)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

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
