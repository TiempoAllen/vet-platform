using Microsoft.AspNetCore.Mvc;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Clients;

/// <summary>Client (pet owner) records of the caller's clinic (docs/API.md section 5). There is no delete: deactivate instead.</summary>
[ApiController]
[Route("api/clients")]
public sealed class ClientsController(ClientService clients) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.ClientsRead)]
    public Task<PagedResponse<ClientResponse>> List([FromQuery] ClientListQuery query, CancellationToken cancellationToken) =>
        clients.ListAsync(query, cancellationToken);

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.ClientsRead)]
    public Task<ClientResponse> Get(Guid id, CancellationToken cancellationToken) =>
        clients.GetAsync(id, cancellationToken);

    [HttpPost]
    [RequirePermission(Permissions.ClientsManage)]
    public async Task<ActionResult<ClientResponse>> Create(CreateClientRequest request, CancellationToken cancellationToken)
    {
        var client = await clients.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = client.Id }, client);
    }

    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.ClientsManage)]
    public Task<ClientResponse> Update(Guid id, UpdateClientRequest request, CancellationToken cancellationToken) =>
        clients.UpdateAsync(id, request, cancellationToken);
}
