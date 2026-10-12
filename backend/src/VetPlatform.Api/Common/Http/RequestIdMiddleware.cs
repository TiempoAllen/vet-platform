namespace VetPlatform.Api.Common.Http;

/// <summary>
/// Gives every request a correlation id, exposed as <c>HttpContext.TraceIdentifier</c>, the
/// <c>X-Request-Id</c> response header, the <c>requestId</c> field of error responses, and the
/// <c>CorrelationId</c> logging scope. A caller-supplied id is reused only if it is short and uses a safe character set
/// (prevents log injection); otherwise a new one is generated.
/// </summary>
public sealed class RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
{
    public const string HeaderName = "X-Request-Id";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        var requestId = IsAcceptable(incoming) ? incoming! : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = requestId;

        // OnStarting so the header survives the exception handler clearing the response.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        // Named CorrelationId (not RequestId) because the host already adds a "RequestId" scope
        // whose value is Kestrel's connection-scoped id, which is a different value.
        using (logger.BeginScope("CorrelationId:{CorrelationId}", requestId))
        {
            await next(context);
        }
    }

    private static bool IsAcceptable(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
