using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Users;

/// <summary>
/// Staff management within the caller's clinic (docs/API.md section 4). Every query runs under the clinic query
/// filter, so another clinic's users are simply not found (404). Changes and their audit entries commit together.
/// A clinic must always keep at least one active administrator.
/// </summary>
/// <remarks>
/// Every change takes the clinic row lock first and then re-checks, from the database, that the caller may still
/// manage users: the permission check at the door used roles read before the lock, and another administrator may
/// have demoted or disabled the caller while this request waited.
/// </remarks>
public sealed class UserService(
    AppDbContext db,
    ITenantContext tenant,
    IPasswordHasher<User> passwordHasher,
    AuditTrail audit,
    AuthService authService,
    TimeProvider timeProvider)
{
    public Task<PagedResponse<UserResponse>> ListAsync(UserListQuery query, CancellationToken cancellationToken)
    {
        var users = db.Users.AsNoTracking().Include(user => user.Roles).AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = PagingExtensions.ContainsPattern(query.Q.Trim());
            // Email uses a nondeterministic collation, which LIKE rejects before PostgreSQL 18; compare it under the
            // default collation instead (it is stored lower-cased, and ILIKE ignores case anyway).
            users = users.Where(user =>
                EF.Functions.ILike(user.Name, pattern, PagingExtensions.LikeEscape)
                || EF.Functions.ILike(EF.Functions.Collate(user.Email, "default"), pattern, PagingExtensions.LikeEscape));
        }

        if (query.ParsedStatus is { } status)
        {
            users = users.Where(user => user.Status == status);
        }

        if (query.ParsedRole is { } role)
        {
            users = users.Where(user => user.Roles.Any(userRole => userRole.Role == role));
        }

        var (field, descending) = SortSpec.Parse(query.Sort);
        users = field switch
        {
            "email" => descending ? users.OrderByDescending(u => u.Email) : users.OrderBy(u => u.Email),
            "createdAt" => descending ? users.OrderByDescending(u => u.CreatedAt) : users.OrderBy(u => u.CreatedAt),
            "lastLoginAt" => descending ? users.OrderByDescending(u => u.LastLoginAt) : users.OrderBy(u => u.LastLoginAt),
            _ => descending ? users.OrderByDescending(u => u.Name) : users.OrderBy(u => u.Name),
        };

        // Id as a tie-breaker keeps pages stable when sort values repeat.
        return ((IOrderedQueryable<User>)users).ThenBy(user => user.Id)
            .AsSplitQuery()
            .ToPageAsync(query, UserResponse.From, cancellationToken);
    }

    public async Task<UserResponse> GetAsync(Guid id, CancellationToken cancellationToken) =>
        UserResponse.From(await FindAsync(id, tracking: false, cancellationToken));

    public async Task<UserResponse> CreateAsync(
        CreateUserRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email!);
        if (PasswordPolicy.Validate(request.Password!, email) is { } problem)
        {
            throw Invalid("password", problem);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockClinicAsync(cancellationToken);
        await EnsureActorCanManageUsersAsync(actorUserId, cancellationToken);
        await EnsureEmailIsFreeAsync(email, exceptUserId: null, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var user = new User
        {
            ClinicId = CurrentClinicId,
            Name = request.Name!.Trim(),
            Email = email,
            Phone = NullIfBlank(request.Phone),
            PasswordHash = "",
            Roles = request.Roles!.Distinct().Select(role => new UserRole { Role = role, CreatedAt = now }).ToList(),
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password!);
        db.Users.Add(user);
        audit.Record(AuditActions.UserCreated, nameof(User), user.Id, new { roles = RoleNames(user) });

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return UserResponse.From(user);
    }

    public async Task<UserResponse> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
        {
            throw Invalid("name", "The name must not be blank.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockClinicAsync(cancellationToken);
        await EnsureActorCanManageUsersAsync(actorUserId, cancellationToken);
        var user = await FindAsync(id, tracking: true, cancellationToken);
        var changed = new List<string>();
        var sessionsSignedOut = 0;

        if (request.Name is { } name && name.Trim() != user.Name)
        {
            user.Name = name.Trim();
            changed.Add("name");
        }

        if (request.Email is { } rawEmail && User.NormalizeEmail(rawEmail) is var email && email != user.Email)
        {
            await EnsureEmailIsFreeAsync(email, exceptUserId: user.Id, cancellationToken);
            user.Email = email;
            changed.Add("email");
        }

        if (request.Phone is not null && NullIfBlank(request.Phone) != user.Phone)
        {
            user.Phone = NullIfBlank(request.Phone);
            changed.Add("phone");
        }

        if (request.Status is { } status && status != user.Status)
        {
            if (status == UserStatus.Disabled)
            {
                if (user.Id == actorUserId)
                {
                    throw Conflict("You cannot disable your own account.");
                }

                await EnsureAnotherActiveAdminAsync(user, cancellationToken);
            }

            user.Status = status;
            changed.Add("status");

            // Revoke rather than merely refuse: re-enabling the account later must not revive old (possibly stolen)
            // sessions.
            if (status == UserStatus.Disabled)
            {
                sessionsSignedOut = await authService.RevokeAllSessionsAsync(
                    user, exceptSessionId: null, reason: null, timeProvider.GetUtcNow(), cancellationToken);
            }
        }

        if (changed.Count > 0)
        {
            audit.Record(
                AuditActions.UserUpdated, nameof(User), user.Id,
                new { fields = changed, status = user.Status, sessionsSignedOut });
            await SaveAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return UserResponse.From(user);
    }

    public async Task<UserResponse> SetRolesAsync(
        Guid id,
        IReadOnlyList<Role> roles,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockClinicAsync(cancellationToken);
        await EnsureActorCanManageUsersAsync(actorUserId, cancellationToken);
        var user = await FindAsync(id, tracking: true, cancellationToken);

        var before = RoleNames(user);
        var wanted = roles.Distinct().ToHashSet();
        if (!wanted.Contains(Role.Admin) && user.Roles.Any(role => role.Role == Role.Admin))
        {
            await EnsureAnotherActiveAdminAsync(user, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var removed = user.Roles.Where(role => !wanted.Contains(role.Role)).ToList();
        db.RemoveRange(removed);
        user.Roles.RemoveAll(removed.Contains);
        user.Roles.AddRange(wanted
            .Where(role => user.Roles.All(existing => existing.Role != role))
            .Select(role => new UserRole { UserId = user.Id, Role = role, CreatedAt = now }));

        if (!before.SequenceEqual(RoleNames(user)))
        {
            // UserRole rows are not Entity-based, so touch the user to record when its access last changed.
            user.UpdatedAt = now;
            audit.Record(AuditActions.UserRolesChanged, nameof(User), user.Id, new { before, after = RoleNames(user) });
            await SaveAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return UserResponse.From(user);
    }

    private Guid CurrentClinicId =>
        tenant.ClinicId ?? throw new InvalidOperationException("User management requires a current clinic.");

    private async Task<User> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var users = tracking ? db.Users : db.Users.AsNoTracking();
        return await users.Include(user => user.Roles).SingleOrDefaultAsync(user => user.Id == id, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The user was not found.");
    }

    /// <summary>
    /// Serialises role and status changes within a clinic (row lock on the clinic, held until commit), so two
    /// administrators demoting each other at the same moment cannot leave the clinic without an administrator.
    /// </summary>
    private Task LockClinicAsync(CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT 1 FROM clinics WHERE id = {CurrentClinicId} FOR UPDATE", cancellationToken);

    /// <summary>Re-checks under the clinic lock that the caller is still an active user allowed to manage users.</summary>
    private async Task EnsureActorCanManageUsersAsync(Guid actorUserId, CancellationToken cancellationToken)
    {
        var roles = await db.Users
            .Where(user => user.Id == actorUserId && user.Status == UserStatus.Active)
            .SelectMany(user => user.Roles.Select(role => role.Role))
            .ToListAsync(cancellationToken);

        if (!Permissions.Grant(roles, Permissions.UsersManage))
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "You do not have permission to perform this action.");
        }
    }

    private async Task EnsureAnotherActiveAdminAsync(User user, CancellationToken cancellationToken)
    {
        if (user.Status != UserStatus.Active || user.Roles.All(role => role.Role != Role.Admin))
        {
            return; // removing it does not reduce the number of active administrators
        }

        var otherAdmins = await db.Users.CountAsync(
            other => other.Id != user.Id
                && other.Status == UserStatus.Active
                && other.Roles.Any(role => role.Role == Role.Admin),
            cancellationToken);

        if (otherAdmins == 0)
        {
            throw Conflict("The clinic must keep at least one active administrator.");
        }
    }

    /// <summary>Email is unique across all clinics (ADR-003), so this deliberately looks beyond the current clinic.</summary>
    private async Task EnsureEmailIsFreeAsync(string email, Guid? exceptUserId, CancellationToken cancellationToken)
    {
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email && u.Id != exceptUserId, cancellationToken))
        {
            throw EmailTaken();
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw EmailTaken(); // lost a race with a concurrent create or rename
        }
    }

    private static List<string> RoleNames(User user) =>
        user.Roles.Select(role => role.Role.ToString()).Order(StringComparer.Ordinal).ToList();

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ApiException Invalid(string field, string message) =>
        new(StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.",
            [new ApiErrorDetail(field, message)]);

    private static ApiException Conflict(string message) =>
        new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, message);

    private static ApiException EmailTaken() =>
        new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "A user with this email already exists.",
            [new ApiErrorDetail("email", "A user with this email already exists.")]);
}
