using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Attachments;

/// <summary>
/// Attachments (docs/API.md section 9, ADR-015). Uploads are <c>multipart/form-data</c> with one <c>file</c> part and
/// an optional <c>description</c>. The permission at the door follows the owner in the route; for
/// <c>/api/attachments/{id}</c> the service checks it against the attachment's owner. There is no edit and no delete.
/// </summary>
[ApiController]
public sealed class AttachmentsController(AttachmentService attachments) : ControllerBase
{
    /// <summary>The largest file plus room for the multipart framing and the description.</summary>
    private const long MaxRequestBytes = Attachment.MaxSizeBytes + 64 * 1024;

    // ---- Uploads, one route per owner so the permission is checked before the body is read ----

    [HttpPost("api/patients/{patientId:guid}/attachments")]
    [RequirePermission(Permissions.ClinicalWrite)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ReadsFormItself]
    public Task<ActionResult<AttachmentResponse>> UploadForPatient(Guid patientId, CancellationToken cancellationToken) =>
        UploadAsync(AttachmentOwnerType.Patient, patientId, cancellationToken);

    [HttpPost("api/consultations/{consultationId:guid}/attachments")]
    [RequirePermission(Permissions.ClinicalWrite)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ReadsFormItself]
    public Task<ActionResult<AttachmentResponse>> UploadForConsultation(Guid consultationId, CancellationToken cancellationToken) =>
        UploadAsync(AttachmentOwnerType.Consultation, consultationId, cancellationToken);

    [HttpPost("api/clients/{clientId:guid}/attachments")]
    [RequirePermission(Permissions.ClientsManage)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ReadsFormItself]
    public Task<ActionResult<AttachmentResponse>> UploadForClient(Guid clientId, CancellationToken cancellationToken) =>
        UploadAsync(AttachmentOwnerType.Client, clientId, cancellationToken);

    // ---- Lists ----

    [HttpGet("api/patients/{patientId:guid}/attachments")]
    [RequirePermission(Permissions.ClinicalRead)]
    public Task<PagedResponse<AttachmentResponse>> ListForPatient(Guid patientId, [FromQuery] AttachmentListQuery query, CancellationToken cancellationToken) =>
        attachments.ListForPatientAsync(patientId, query, cancellationToken);

    [HttpGet("api/consultations/{consultationId:guid}/attachments")]
    [RequirePermission(Permissions.ClinicalRead)]
    public Task<PagedResponse<AttachmentResponse>> ListForConsultation(Guid consultationId, [FromQuery] AttachmentListQuery query, CancellationToken cancellationToken) =>
        attachments.ListForConsultationAsync(consultationId, query, cancellationToken);

    [HttpGet("api/clients/{clientId:guid}/attachments")]
    [RequirePermission(Permissions.ClientsRead)]
    public Task<PagedResponse<AttachmentResponse>> ListForClient(Guid clientId, [FromQuery] AttachmentListQuery query, CancellationToken cancellationToken) =>
        attachments.ListForClientAsync(clientId, query, cancellationToken);

    // ---- One attachment ----

    [HttpGet("api/attachments/{id:guid}")]
    [RequireAnyPermission(Permissions.ClinicalRead, Permissions.ClientsRead)]
    public Task<AttachmentResponse> Get(Guid id, CancellationToken cancellationToken) =>
        attachments.GetAsync(id, cancellationToken);

    /// <summary>
    /// Always a download (<c>Content-Disposition: attachment</c>), never rendered by the API's origin: the response is
    /// sandboxed, not sniffed and not cached.
    /// </summary>
    [HttpGet("api/attachments/{id:guid}/content")]
    [RequireAnyPermission(Permissions.ClinicalRead, Permissions.ClientsRead)]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var (content, contentType, fileName) = await attachments.DownloadAsync(id, cancellationToken);
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        Response.Headers.CacheControl = "private, no-store";
        return File(content, contentType, fileName);
    }

    [HttpPost("api/attachments/{id:guid}/withdraw")]
    [RequireAnyPermission(Permissions.ClinicalWrite, Permissions.ClientsManage)]
    public Task<AttachmentResponse> Withdraw(Guid id, WithdrawAttachmentRequest request, CancellationToken cancellationToken) =>
        attachments.WithdrawAsync(id, request, cancellationToken);

    private async Task<ActionResult<AttachmentResponse>> UploadAsync(
        AttachmentOwnerType ownerType,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var attachment = await attachments.UploadAsync(ownerType, ownerId, await ReadUploadAsync(cancellationToken), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = attachment.Id }, attachment);
    }

    /// <summary>Reads the multipart form: 415 if it is not one, 413 if it exceeds the limit, 400 if it is malformed.</summary>
    private async Task<AttachmentUpload> ReadUploadAsync(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            throw new ApiException(
                StatusCodes.Status415UnsupportedMediaType, ErrorCodes.UnsupportedMediaType, "Send the file as multipart/form-data.");
        }

        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw AttachmentService.TooLarge();
        }
        catch (InvalidDataException exception) when (exception.Message.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            throw AttachmentService.TooLarge();
        }
        catch (InvalidDataException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "The multipart form is malformed.");
        }

        var description = form.TryGetValue("description", out var values) ? values.ToString() : null;
        return new AttachmentUpload(form.Files.GetFile("file"), form.Files.Count, description);
    }
}

/// <summary>
/// Keeps MVC's form value providers from reading an upload before the action does: they would turn a form over the
/// size limit into a generic 400 instead of the 413 the action reports.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ReadsFormItselfAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        context.ValueProviderFactories.RemoveType<FormValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<FormFileValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
