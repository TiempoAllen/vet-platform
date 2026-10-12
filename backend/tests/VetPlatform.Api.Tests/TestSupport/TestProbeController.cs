using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Errors;

namespace VetPlatform.Api.Tests.TestSupport;

/// <summary>Test-only endpoints that trigger each error path. Lives in the test assembly, never shipped.</summary>
[ApiController]
[Route("api/test-probe")]
public sealed class TestProbeController : ControllerBase
{
    public const string SecretMessage = "secret internal detail for patient Rex";

    public sealed record Sample([Required] string? Name, [Range(0, 30)] int Age);

    [HttpGet("ok")]
    public IActionResult Probe() => Ok(new { ok = true });

    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException(SecretMessage);

    [HttpGet("conflict")]
    public IActionResult ThrowConflict() =>
        throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Already exists.");

    [HttpGet("bare-not-found")]
    public IActionResult BareNotFound() => NotFound();

    [HttpPost("validate")]
    public IActionResult Validate([FromBody] Sample body) => Ok(body);
}
