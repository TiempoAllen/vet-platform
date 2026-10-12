using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Features.Auth;

namespace VetPlatform.Api.Features.Users;

/// <summary>Staff management for the caller's clinic (docs/API.md section 4).</summary>
[ApiController]
[Route("api/users")]
public sealed class UsersController(UserService users) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.UsersRead)]
    public Task<PagedResponse<UserResponse>> List([FromQuery] UserListQuery query, CancellationToken cancellationToken) =>
        users.ListAsync(query, cancellationToken);

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.UsersRead)]
    public Task<UserResponse> Get(Guid id, CancellationToken cancellationToken) =>
        users.GetAsync(id, cancellationToken);

    [HttpPost]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await users.CreateAsync(request, CallerId, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, user);
    }

    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public Task<UserResponse> Update(Guid id, UpdateUserRequest request, CancellationToken cancellationToken) =>
        users.UpdateAsync(id, request, CallerId, cancellationToken);

    /// <summary>Replaces the user's roles with exactly the given set.</summary>
    [HttpPost("{id:guid}/roles")]
    [RequirePermission(Permissions.UsersManage)]
    public Task<UserResponse> SetRoles(Guid id, SetRolesRequest request, CancellationToken cancellationToken) =>
        users.SetRolesAsync(id, request.Roles!, CallerId, cancellationToken);

    /// <summary>The signed-in caller (always present: these endpoints require a permission).</summary>
    private Guid CallerId => AuthClaims.GetUserId(User)!.Value;
}
