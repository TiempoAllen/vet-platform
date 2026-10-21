using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;

namespace VetPlatform.Api.Features.Clients;

/// <summary>
/// Client records within the caller's clinic (docs/API.md section 5). Every query runs under the clinic query filter,
/// so another clinic's clients are simply not found (404). Changes and their audit entries commit together. Clients
/// are never deleted, only deactivated (FR-CLI-3).
/// </summary>
/// <remarks>
/// Audit metadata names the changed fields but never their values: client contact details are personal data that
/// the audit log does not need.
/// </remarks>
public sealed class ClientService(AppDbContext db, AuditTrail audit)
{
    public Task<PagedResponse<ClientResponse>> ListAsync(ClientListQuery query, CancellationToken cancellationToken)
    {
        var clients = db.Clients.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = PagingExtensions.ContainsPattern(query.Q.Trim());
            clients = clients.Where(client =>
                EF.Functions.ILike(client.FirstName, pattern, PagingExtensions.LikeEscape)
                || EF.Functions.ILike(client.LastName, pattern, PagingExtensions.LikeEscape)
                || EF.Functions.ILike(client.FirstName + " " + client.LastName, pattern, PagingExtensions.LikeEscape)
                || EF.Functions.ILike(client.Email!, pattern, PagingExtensions.LikeEscape)
                || EF.Functions.ILike(client.Phone!, pattern, PagingExtensions.LikeEscape));
        }

        if (query.ParsedStatus is { } status)
        {
            clients = clients.Where(client => client.Status == status);
        }

        var (field, descending) = SortSpec.Parse(query.Sort);
        var ordered = field switch
        {
            "createdAt" => descending ? clients.OrderByDescending(c => c.CreatedAt) : clients.OrderBy(c => c.CreatedAt),
            "updatedAt" => descending ? clients.OrderByDescending(c => c.UpdatedAt) : clients.OrderBy(c => c.UpdatedAt),
            _ => descending
                ? clients.OrderByDescending(c => c.LastName).ThenByDescending(c => c.FirstName)
                : clients.OrderBy(c => c.LastName).ThenBy(c => c.FirstName),
        };

        // Id as a tie-breaker keeps pages stable when sort values repeat. It follows the sort direction so the
        // (clinic_id, <sort column(s)>, id) indexes can serve both directions.
        return (descending ? ordered.ThenByDescending(client => client.Id) : ordered.ThenBy(client => client.Id))
            .ToPageAsync(query, ClientResponse.From, cancellationToken);
    }

    public async Task<ClientResponse> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ClientResponse.From(await FindAsync(id, tracking: false, cancellationToken));

    public async Task<ClientResponse> CreateAsync(CreateClientRequest request, CancellationToken cancellationToken)
    {
        // The clinic is stamped by ClinicScopeInterceptor from the session; request bodies never carry one.
        var client = new Client
        {
            FirstName = request.FirstName!.Trim(),
            LastName = request.LastName!.Trim(),
            Email = NormalizeEmail(request.Email),
            Phone = NullIfBlank(request.Phone),
            AddressLine1 = NullIfBlank(request.AddressLine1),
            AddressLine2 = NullIfBlank(request.AddressLine2),
            City = NullIfBlank(request.City),
            Region = NullIfBlank(request.Region),
            PostalCode = NullIfBlank(request.PostalCode),
            Country = NullIfBlank(request.Country),
            PreferredContactMethod = request.PreferredContactMethod,
            Notes = NullIfBlank(request.Notes),
        };
        db.Clients.Add(client);
        audit.Record(AuditActions.ClientCreated, nameof(Client), client.Id);

        await db.SaveChangesAsync(cancellationToken);
        return ClientResponse.From(client);
    }

    public async Task<ClientResponse> UpdateAsync(Guid id, UpdateClientRequest request, CancellationToken cancellationToken)
    {
        var client = await FindAsync(id, tracking: true, cancellationToken);
        var changed = new List<string>();

        void Set<T>(string field, T current, T wanted, Action<T> apply)
        {
            if (!EqualityComparer<T>.Default.Equals(current, wanted))
            {
                apply(wanted);
                changed.Add(field);
            }
        }

        // Required names: null leaves them unchanged (blank was rejected by validation).
        if (request.FirstName is { } firstName)
        {
            Set("firstName", client.FirstName, firstName.Trim(), value => client.FirstName = value);
        }

        if (request.LastName is { } lastName)
        {
            Set("lastName", client.LastName, lastName.Trim(), value => client.LastName = value);
        }

        // Optional text: null leaves it unchanged, blank clears it.
        if (request.Email is not null)
        {
            Set("email", client.Email, NormalizeEmail(request.Email), value => client.Email = value);
        }

        SetText("phone", request.Phone, client.Phone, value => client.Phone = value);
        SetText("addressLine1", request.AddressLine1, client.AddressLine1, value => client.AddressLine1 = value);
        SetText("addressLine2", request.AddressLine2, client.AddressLine2, value => client.AddressLine2 = value);
        SetText("city", request.City, client.City, value => client.City = value);
        SetText("region", request.Region, client.Region, value => client.Region = value);
        SetText("postalCode", request.PostalCode, client.PostalCode, value => client.PostalCode = value);
        SetText("country", request.Country, client.Country, value => client.Country = value);
        SetText("notes", request.Notes, client.Notes, value => client.Notes = value);

        if (request.PreferredContactMethod.IsSet)
        {
            Set(
                "preferredContactMethod", client.PreferredContactMethod, request.PreferredContactMethod.Value,
                value => client.PreferredContactMethod = value);
        }

        if (request.Status is { } status)
        {
            Set("status", client.Status, status, value => client.Status = value);
        }

        if (changed.Count > 0)
        {
            audit.Record(AuditActions.ClientUpdated, nameof(Client), client.Id, new { fields = changed, status = client.Status });
            await db.SaveChangesAsync(cancellationToken);
        }

        return ClientResponse.From(client);

        void SetText(string field, string? requested, string? current, Action<string?> apply)
        {
            if (requested is not null)
            {
                Set(field, current, NullIfBlank(requested), apply);
            }
        }
    }

    private async Task<Client> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var clients = tracking ? db.Clients : db.Clients.AsNoTracking();
        return await clients.SingleOrDefaultAsync(client => client.Id == id, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The client was not found.");
    }

    private static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : Client.NormalizeEmail(email);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
