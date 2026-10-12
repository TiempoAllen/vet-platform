namespace VetPlatform.Api.Common.Errors;

/// <summary>Single place that builds and writes the standard error envelope.</summary>
public static class ErrorResponseWriter
{
    public static ApiErrorResponse Build(
        HttpContext context,
        string code,
        string message,
        IReadOnlyList<ApiErrorDetail>? details = null) =>
        new(new ApiErrorBody(code, message, details ?? []), context.TraceIdentifier);

    public static Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        IReadOnlyList<ApiErrorDetail>? details = null,
        CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(Build(context, code, message, details), cancellationToken);
    }

    /// <summary>Writes a generic envelope for a bare status code (404 for unknown routes, 401/403, 405, ...).</summary>
    public static Task WriteForStatusAsync(HttpContext context)
    {
        var statusCode = context.Response.StatusCode;
        var (code, message) = ErrorCodes.ForStatus(statusCode);
        return WriteAsync(context, statusCode, code, message, cancellationToken: context.RequestAborted);
    }
}
