using VetPlatform.Api.Features.Auth;

namespace VetPlatform.Api.Common.Tenancy;

/// <summary>Marks an entity as owned by one clinic. Its rows are only visible and writable within that clinic (ADR-004).</summary>
public interface IClinicOwned
{
    Guid ClinicId { get; set; }
}

/// <summary>The clinic the current request acts for, or null when there is none (anonymous, CLI, tests).</summary>
public interface ITenantContext
{
    Guid? ClinicId { get; }
}

/// <summary>
/// Reads the clinic from the signed-in principal. The claim is trustworthy because session validation
/// (<see cref="SessionCookieEvents"/>) rejects cookies whose clinic claim does not match the session row.
/// While that validation itself runs, the principal is still anonymous, so the clinic is null.
/// </summary>
public sealed class HttpTenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    public Guid? ClinicId =>
        httpContextAccessor.HttpContext?.User is { Identity.IsAuthenticated: true } user
            ? AuthClaims.GetClinicId(user)
            : null;
}
