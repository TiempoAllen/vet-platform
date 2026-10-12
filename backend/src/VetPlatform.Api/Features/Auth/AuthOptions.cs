namespace VetPlatform.Api.Features.Auth;

/// <summary>Session and sign-in protection settings, bound from the <c>Auth</c> configuration section.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>A session ends after this long without any request.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>A session ends this long after sign-in, however active it is.</summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Consecutive wrong passwords that lock an account temporarily.</summary>
    public int MaxFailedLogins { get; set; } = 5;

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Sign-in attempts allowed per client IP address per minute, across all accounts.</summary>
    public int LoginAttemptsPerMinute { get; set; } = 10;
}
