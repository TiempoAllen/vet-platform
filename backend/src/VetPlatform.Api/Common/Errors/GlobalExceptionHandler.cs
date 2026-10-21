using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace VetPlatform.Api.Common.Errors;

/// <summary>
/// Converts any exception escaping the pipeline into the standard error envelope.
/// Unexpected exceptions are logged server-side and returned as a generic 500:
/// no stack traces, exception types, or messages ever reach the client.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Non-standard (nginx) status for "client closed the request"; only ever seen in our own logs.</summary>
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                logger.LogDebug("Request was cancelled by the client.");
                httpContext.Response.StatusCode = ClientClosedRequest;
                return true;

            case ApiException apiException:
                logger.LogInformation(
                    "Request failed with {ErrorCode} ({StatusCode}).",
                    apiException.Code,
                    apiException.StatusCode);
                await ErrorResponseWriter.WriteAsync(
                    httpContext,
                    apiException.StatusCode,
                    apiException.Code,
                    apiException.Message,
                    apiException.Details,
                    cancellationToken);
                return true;

            case BadHttpRequestException badRequest:
                logger.LogInformation("Rejected bad HTTP request with status {StatusCode}.", badRequest.StatusCode);
                var (badCode, badMessage) = ErrorCodes.ForStatus(badRequest.StatusCode);
                await ErrorResponseWriter.WriteAsync(
                    httpContext, badRequest.StatusCode, badCode, badMessage, cancellationToken: cancellationToken);
                return true;

            case Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException:
                logger.LogInformation("A record was changed or removed concurrently.");
                await ErrorResponseWriter.WriteAsync(
                    httpContext,
                    StatusCodes.Status409Conflict,
                    ErrorCodes.Conflict,
                    "The record was changed or removed by someone else. Reload it and try again.",
                    cancellationToken: cancellationToken);
                return true;

            case Exception when IsUnstorableText(exception):
                logger.LogInformation("Rejected a request containing text the database cannot store.");
                await ErrorResponseWriter.WriteAsync(
                    httpContext,
                    StatusCodes.Status400BadRequest,
                    ErrorCodes.BadRequest,
                    "The request contains characters that cannot be stored.",
                    cancellationToken: cancellationToken);
                return true;

            default:
                logger.LogError(
                    exception,
                    "Unhandled exception while processing {Method} {Path}.",
                    httpContext.Request.Method,
                    httpContext.Request.Path.Value);
                await ErrorResponseWriter.WriteAsync(
                    httpContext,
                    StatusCodes.Status500InternalServerError,
                    ErrorCodes.InternalError,
                    "An unexpected error occurred.",
                    cancellationToken: cancellationToken);
                return true;
        }
    }

    /// <summary>
    /// PostgreSQL refuses NUL characters in text (SQLSTATE 22021), whether they came in a JSON body or a query string.
    /// EF Core wraps the error in a <c>DbUpdateException</c> on save, so the whole chain is searched.
    /// </summary>
    private static bool IsUnstorableText(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is PostgresException { SqlState: PostgresErrorCodes.CharacterNotInRepertoire })
            {
                return true;
            }
        }

        return false;
    }
}
