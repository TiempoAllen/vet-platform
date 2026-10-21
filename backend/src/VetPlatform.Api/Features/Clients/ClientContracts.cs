using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Clients;

/// <summary>A client as returned by the clients API (docs/API.md section 5).</summary>
public sealed record ClientResponse(
    Guid Id,
    string FirstName,
    string LastName,
    string? Email,
    string? Phone,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? Region,
    string? PostalCode,
    string? Country,
    ContactMethod? PreferredContactMethod,
    string? Notes,
    ClientStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ClientResponse From(Client client) => new(
        client.Id, client.FirstName, client.LastName, client.Email, client.Phone,
        client.AddressLine1, client.AddressLine2, client.City, client.Region, client.PostalCode, client.Country,
        client.PreferredContactMethod, client.Notes, client.Status, client.CreatedAt, client.UpdatedAt);
}

public sealed class ClientListQuery : PageQuery, IValidatableObject
{
    public static readonly string[] SortFields = ["name", "createdAt", "updatedAt"];

    /// <summary>Matches first name, last name, full name, email or phone, case-insensitively, anywhere in the value.</summary>
    [MaxLength(100)]
    public string? Q { get; init; }

    /// <summary><c>Active</c> or <c>Inactive</c>. A string so only exact names are accepted (see <see cref="StrictEnum"/>).</summary>
    public string? Status { get; init; }

    public ClientStatus? ParsedStatus => StrictEnum.TryParse<ClientStatus>(Status, out var status) ? status : null;

    /// <summary><c>name</c> (last name, then first name; default), <c>createdAt</c> or <c>updatedAt</c>; prefix with <c>-</c> for descending.</summary>
    public string Sort { get; init; } = "name";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<ClientStatus>()}.", [nameof(Status)]);
        }

        if (!SortFields.Contains(SortSpec.Parse(Sort).Field, StringComparer.Ordinal))
        {
            yield return new ValidationResult(
                $"Sort must be one of {string.Join(", ", SortFields)}, optionally prefixed with '-'.", [nameof(Sort)]);
        }
    }
}

/// <summary>Optional text fields may be omitted, null or blank; all three store nothing.</summary>
public sealed record CreateClientRequest(
    [Required, MaxLength(Client.NameMaxLength)] string? FirstName,
    [Required, MaxLength(Client.NameMaxLength)] string? LastName,
    [MaxLength(Client.EmailMaxLength)] string? Email,
    [MaxLength(Client.PhoneMaxLength)] string? Phone,
    [MaxLength(Client.AddressLineMaxLength)] string? AddressLine1,
    [MaxLength(Client.AddressLineMaxLength)] string? AddressLine2,
    [MaxLength(Client.LocalityMaxLength)] string? City,
    [MaxLength(Client.LocalityMaxLength)] string? Region,
    [MaxLength(Client.PostalCodeMaxLength)] string? PostalCode,
    [MaxLength(Client.LocalityMaxLength)] string? Country,
    ContactMethod? PreferredContactMethod,
    [MaxLength(Client.NotesMaxLength)] string? Notes) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ClientValidation.Email(Email);
}

/// <summary>
/// Partial update: omitted or null text fields are left unchanged and <c>""</c> clears an optional one.
/// <c>preferredContactMethod</c> is cleared with an explicit <c>null</c>.
/// </summary>
public sealed record UpdateClientRequest(
    [MaxLength(Client.NameMaxLength)] string? FirstName,
    [MaxLength(Client.NameMaxLength)] string? LastName,
    [MaxLength(Client.EmailMaxLength)] string? Email,
    [MaxLength(Client.PhoneMaxLength)] string? Phone,
    [MaxLength(Client.AddressLineMaxLength)] string? AddressLine1,
    [MaxLength(Client.AddressLineMaxLength)] string? AddressLine2,
    [MaxLength(Client.LocalityMaxLength)] string? City,
    [MaxLength(Client.LocalityMaxLength)] string? Region,
    [MaxLength(Client.PostalCodeMaxLength)] string? PostalCode,
    [MaxLength(Client.LocalityMaxLength)] string? Country,
    Optional<ContactMethod?> PreferredContactMethod,
    [MaxLength(Client.NotesMaxLength)] string? Notes,
    ClientStatus? Status) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FirstName is not null && string.IsNullOrWhiteSpace(FirstName))
        {
            yield return new ValidationResult("The first name must not be blank.", [nameof(FirstName)]);
        }

        if (LastName is not null && string.IsNullOrWhiteSpace(LastName))
        {
            yield return new ValidationResult("The last name must not be blank.", [nameof(LastName)]);
        }

        foreach (var result in ClientValidation.Email(Email))
        {
            yield return result;
        }
    }
}

internal static class ClientValidation
{
    private static readonly EmailAddressAttribute EmailFormat = new();

    /// <summary>A blank email means "none"; anything else must look like an address.</summary>
    public static IEnumerable<ValidationResult> Email(string? email)
    {
        if (!string.IsNullOrWhiteSpace(email) && !EmailFormat.IsValid(email.Trim()))
        {
            yield return new ValidationResult("The email is not a valid email address.", ["Email"]);
        }
    }
}
