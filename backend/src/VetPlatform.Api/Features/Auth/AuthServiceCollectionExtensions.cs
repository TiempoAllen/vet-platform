using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Security;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Auth;

public static class AuthServiceCollectionExtensions
{
    public const string LoginRateLimitPolicy = "login";

    /// <summary>The <c>__Host-</c> prefix makes browsers require Secure, Path=/ and no Domain.</summary>
    public const string SessionCookieName = "__Host-vet_session";

    public static IServiceCollection AddAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(o => o.IdleTimeout > TimeSpan.Zero && o.AbsoluteLifetime >= o.IdleTimeout,
                "Auth:IdleTimeout must be positive and no longer than Auth:AbsoluteLifetime.")
            .Validate(o => o.MaxFailedLogins > 0 && o.LockoutDuration > TimeSpan.Zero && o.LoginAttemptsPerMinute > 0,
                "Auth lockout and rate-limit settings must be positive.")
            .ValidateOnStart();

        services.AddScoped<AuthService>();
        services.AddScoped<SessionCookieEvents>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = SessionCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.Cookie.IsEssential = true;
                // The session row is authoritative (idle and absolute limits); the cookie ticket only needs to
                // outlive it. Browser-session cookie: IsPersistent is false at sign-in.
                options.SlidingExpiration = false;
                options.EventsType = typeof(SessionCookieEvents);
            });

        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((cookie, auth) => cookie.ExpireTimeSpan = auth.Value.AbsoluteLifetime);

        // Every controller endpoint requires a signed-in user unless it opts out with [AllowAnonymous]; see
        // MapControllers().RequireAuthorization() in Program.cs. Role and permission checks are added in B-04.
        services.AddAuthorization();

        services.AddAntiforgery(CsrfProtection.Configure);

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(LoginRateLimitPolicy, context =>
            {
                var permits = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value
                    .LoginAttemptsPerMinute;
                // Behind a reverse proxy this is the proxy's address until forwarded headers are configured (X-05).
                var partition = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }

                await ErrorResponseWriter.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status429TooManyRequests,
                    ErrorCodes.RateLimited,
                    "Too many sign-in attempts. Please wait a minute and try again.",
                    cancellationToken: cancellationToken);
            };
        });

        return services;
    }
}
