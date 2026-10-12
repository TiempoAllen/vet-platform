namespace VetPlatform.Api.Common.Errors;

/// <summary>
/// Throw from services/controllers to produce a specific, client-safe error response.
/// The message and details are returned to the client, so never put secrets or
/// internal/sensitive data in them.
/// </summary>
public class ApiException(
    int statusCode,
    string code,
    string message,
    IReadOnlyList<ApiErrorDetail>? details = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public IReadOnlyList<ApiErrorDetail> Details { get; } = details ?? [];
}
