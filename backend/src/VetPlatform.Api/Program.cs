using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Security;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Clients;
using VetPlatform.Api.Features.Patients;
using VetPlatform.Api.Features.Scheduling;
using VetPlatform.Api.Features.Users;

var builder = WebApplication.CreateBuilder(args);

// Logging format (json in production, readable in Development) comes from appsettings.*.json.
// Configuration precedence: appsettings.json < appsettings.{Environment}.json < environment
// variables < command line. Secrets must come from environment/secret stores, never from files in git.

builder.Services
    .AddControllers(options => options.Filters.Add<CsrfProtectionFilter>())
    // Enums travel only as their exact names ("Admin", "Active"), matching how they are stored and documented.
    // Instants must carry an explicit offset ("...Z" or "...+01:00"); otherwise the server's zone would be assumed.
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new StrictEnumConverterFactory());
        options.JsonSerializerOptions.Converters.Add(new StrictInstantConverter());
    })
    .ConfigureApiBehaviorOptions(ApiBehaviorConfiguration.Configure);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddPersistence();
builder.Services.AddAuth(builder.Configuration);
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<ClientService>();
builder.Services.AddScoped<PatientService>();
builder.Services.AddScoped<PatientHistoryService>();
builder.Services.AddScoped<ScheduleService>();

// /api/health is Unhealthy (503) when the database cannot be reached. The timeout keeps a hung connection
// attempt from stalling health probes.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();

PersistenceServiceCollectionExtensions.EnsureDatabaseConfigured(app.Configuration);

// Administrative commands run instead of the web server, e.g. `dotnet run -- create-admin ...` (backend/README.md).
if (args is [CreateAdminCommand.Name, .. var commandArgs])
{
    Environment.ExitCode = await CreateAdminCommand.RunAsync(app.Services, commandArgs, Console.In, Console.Out);
    return;
}

// Order matters: request id first so everything below (logs, error bodies) can use it;
// request logging wraps the exception handler so it logs the final status code.
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
// GlobalExceptionHandler handles everything; the empty fallback pipeline only exists because the
// middleware refuses to start without one. If it were ever reached, the status code pages
// middleware below still produces a generic 500 envelope.
app.UseExceptionHandler(fallback => fallback.Run(_ => Task.CompletedTask));
app.UseStatusCodePages(statusContext => ErrorResponseWriter.WriteForStatusAsync(statusContext.HttpContext));

// Tell browsers to use HTTPS only. Not in Development, where HSTS on localhost would stick to other local projects.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Rate limiting runs before authentication so throttled sign-in attempts cost no database work.
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Secure default: every controller endpoint requires a signed-in user unless marked [AllowAnonymous]. Applied to
// endpoints (not as a fallback policy) so unknown routes still return 404 and wrong methods 405.
app.MapControllers().RequireAuthorization();

// Anonymous by design: reports only overall status, never configuration or dependency details.
app.MapHealthChecks("/api/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = (context, report) =>
        context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() }, context.RequestAborted),
}).AllowAnonymous();

app.Run();

// Exposes the entry point to the test project (WebApplicationFactory<Program>).
public partial class Program;
