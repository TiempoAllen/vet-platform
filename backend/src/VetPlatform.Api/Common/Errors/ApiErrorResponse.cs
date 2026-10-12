namespace VetPlatform.Api.Common.Errors;

/// <summary>
/// Error envelope defined in docs/API.md section 2:
/// { "error": { "code", "message", "details": [] }, "requestId" }
/// </summary>
public sealed record ApiErrorResponse(ApiErrorBody Error, string RequestId);

public sealed record ApiErrorBody(string Code, string Message, IReadOnlyList<ApiErrorDetail> Details);

/// <summary>A single problem, optionally tied to a request field (e.g. "address.city").</summary>
public sealed record ApiErrorDetail(string? Field, string Message);
