using Microsoft.AspNetCore.Mvc;

namespace VetPlatform.Api.Common.Errors;

/// <summary>
/// Makes [ApiController] behaviour use the standard error envelope instead of framework ProblemDetails.
///   - 422 VALIDATION_ERROR: body parsed, but field values are invalid (details list the fields).
///   - 400 BAD_REQUEST: body missing or not parseable. Parser internals are deliberately not echoed.
///   - Empty 4xx results (e.g. <c>return NotFound();</c>) are left bare so the status-code pages
///     middleware turns them into the same envelope.
/// </summary>
public static class ApiBehaviorConfiguration
{
    public static void Configure(ApiBehaviorOptions options)
    {
        options.InvalidModelStateResponseFactory = Create;
        options.SuppressMapClientErrors = true;
    }

    private static IActionResult Create(ActionContext context)
    {
        var modelState = context.ModelState;

        // Body binding problems are keyed "" or "$" / "$.path"; field validation errors use property names.
        var malformedBody = modelState.Any(entry =>
            entry.Value is { Errors.Count: > 0 } && (entry.Key.Length == 0 || entry.Key.StartsWith('$')));

        if (malformedBody)
        {
            return new ObjectResult(ErrorResponseWriter.Build(
                context.HttpContext,
                ErrorCodes.BadRequest,
                "The request body is missing or malformed."))
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }

        var details = modelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .SelectMany(entry => entry.Value!.Errors.Select(error => new ApiErrorDetail(
                ToFieldName(entry.Key),
                string.IsNullOrWhiteSpace(error.ErrorMessage) ? "The value is invalid." : error.ErrorMessage)))
            .ToList();

        return new ObjectResult(ErrorResponseWriter.Build(
            context.HttpContext,
            ErrorCodes.ValidationError,
            "One or more fields are invalid.",
            details))
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
        };
    }

    /// <summary>"Address.StreetLine" -> "address.streetLine" (matches the camelCase JSON the client sends).</summary>
    private static string ToFieldName(string key) =>
        string.Join('.', key.Split('.').Select(segment =>
            segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));
}
