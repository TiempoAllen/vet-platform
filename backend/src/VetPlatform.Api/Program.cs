using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Logging format (json in production, readable in Development) comes from appsettings.*.json.
// Configuration precedence: appsettings.json < appsettings.{Environment}.json < environment
// variables < command line. Secrets must come from environment/secret stores, never from files in git.

builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(ApiBehaviorConfiguration.Configure);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddPersistence();

// /api/health is Unhealthy (503) when the database cannot be reached. The timeout keeps a hung connection
// attempt from stalling health probes.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();

PersistenceServiceCollectionExtensions.EnsureDatabaseConfigured(app.Configuration);

// Order matters: request id first so everything below (logs, error bodies) can use it;
// request logging wraps the exception handler so it logs the final status code.
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
// GlobalExceptionHandler handles everything; the empty fallback pipeline only exists because the
// middleware refuses to start without one. If it were ever reached, the status code pages
// middleware below still produces a generic 500 envelope.
app.UseExceptionHandler(fallback => fallback.Run(_ => Task.CompletedTask));
app.UseStatusCodePages(statusContext => ErrorResponseWriter.WriteForStatusAsync(statusContext.HttpContext));

app.MapControllers();

// Anonymous by design: reports only overall status, never configuration or dependency details.
app.MapHealthChecks("/api/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = (context, report) =>
        context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() }, context.RequestAborted),
});

app.Run();

// Exposes the entry point to the test project (WebApplicationFactory<Program>).
public partial class Program;
