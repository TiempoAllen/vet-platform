using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Tenancy;

namespace VetPlatform.Api.Common.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Configuration key of the PostgreSQL connection string (env var: <c>ConnectionStrings__Default</c>).</summary>
    public const string ConnectionStringName = "Default";

    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        // Every time-dependent rule (timestamps, sessions, lockout) reads the clock from DI so tests can control it.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<TimestampsInterceptor>();
        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContext, HttpTenantContext>();
        services.AddScoped<AuditTrail>();

        // The connection string is read when the context is first created, not at registration, so it comes from
        // the final configuration (including test and environment overrides). Program.cs fails fast if it is missing.
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringName);

            options
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TimestampsInterceptor>(),
                    new ClinicScopeInterceptor(serviceProvider.GetRequiredService<ITenantContext>()))
                // Sessions and role rows hang off clinic-filtered users. They are only ever reached through a user
                // that is already scoped, or by session validation, which deliberately ignores filters.
                .ConfigureWarnings(warnings => warnings.Ignore(
                    CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

            // Never call EnableSensitiveDataLogging: it writes parameter values (client, patient, financial
            // data) into the logs.
        });

        return services;
    }

    /// <summary>Throws at startup when no connection string is configured, instead of failing on the first request.</summary>
    public static void EnsureDatabaseConfigured(IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionStringName)))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is not configured. Set the ConnectionStrings__{ConnectionStringName} " +
                "environment variable, or use dotnet user-secrets in Development (see backend/README.md).");
        }
    }
}
