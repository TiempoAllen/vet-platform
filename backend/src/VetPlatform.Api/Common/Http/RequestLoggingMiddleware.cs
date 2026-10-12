using System.Diagnostics;

namespace VetPlatform.Api.Common.Http;

/// <summary>
/// One structured log line per request: method, path, final status, duration.
/// Privacy: the query string is intentionally NOT logged (search terms may contain client or
/// patient data) and neither are headers or bodies. Health probes log at Debug to avoid noise.
/// </summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            var statusCode = context.Response.StatusCode;
            var level = context.Request.Path.StartsWithSegments("/api/health") ? LogLevel.Debug
                : statusCode >= 500 ? LogLevel.Warning
                : LogLevel.Information;

            logger.Log(
                level,
                "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs} ms",
                context.Request.Method,
                context.Request.Path.Value,
                statusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
