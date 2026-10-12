using System.Net;

namespace VetPlatform.Api.Common.Errors;

/// <summary>Stable, machine-readable error codes. Clients may switch on these; do not rename casually.</summary>
public static class ErrorCodes
{
    public const string BadRequest = "BAD_REQUEST";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string CsrfTokenInvalid = "CSRF_TOKEN_INVALID";
    public const string NotFound = "NOT_FOUND";
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string Conflict = "CONFLICT";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string ValidationError = "VALIDATION_ERROR";
    public const string RateLimited = "RATE_LIMITED";
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>Default code and generic message for a bare HTTP status produced by the framework.</summary>
    public static (string Code, string Message) ForStatus(int statusCode) => statusCode switch
    {
        (int)HttpStatusCode.BadRequest => (BadRequest, "The request is malformed."),
        (int)HttpStatusCode.Unauthorized => (Unauthenticated, "Authentication is required."),
        (int)HttpStatusCode.Forbidden => (Forbidden, "You do not have permission to perform this action."),
        (int)HttpStatusCode.NotFound => (NotFound, "The requested resource was not found."),
        (int)HttpStatusCode.MethodNotAllowed => (MethodNotAllowed, "The HTTP method is not allowed for this resource."),
        (int)HttpStatusCode.Conflict => (Conflict, "The request conflicts with the current state of the resource."),
        (int)HttpStatusCode.UnsupportedMediaType => (UnsupportedMediaType, "The request content type is not supported."),
        (int)HttpStatusCode.UnprocessableEntity => (ValidationError, "The request failed validation."),
        (int)HttpStatusCode.TooManyRequests => (RateLimited, "Too many requests. Please try again later."),
        >= 500 => (InternalError, "An unexpected error occurred."),
        _ => (BadRequest, "The request could not be processed."),
    };
}
