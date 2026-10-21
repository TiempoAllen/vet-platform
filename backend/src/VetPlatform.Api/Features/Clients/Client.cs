using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Clients;

/// <summary>
/// A pet owner (docs/DATABASE.md section 4). Clients are never deleted, because patients, appointments, clinical and
/// financial history will refer to them (FR-CLI-3); they are deactivated instead. Email is not unique: members of one
/// household often share an address.
/// </summary>
public sealed class Client : Entity, IClinicOwned
{
    public const int NameMaxLength = 100;
    public const int EmailMaxLength = 254;
    public const int PhoneMaxLength = 32;
    public const int AddressLineMaxLength = 200;
    public const int LocalityMaxLength = 100;
    public const int PostalCodeMaxLength = 20;
    public const int NotesMaxLength = 4000;

    public Guid ClinicId { get; set; }

    public required string FirstName { get; set; }

    public required string LastName { get; set; }

    /// <summary>Stored trimmed and lower-cased; set it through <see cref="NormalizeEmail"/>.</summary>
    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    /// <summary>State, province or county.</summary>
    public string? Region { get; set; }

    public string? PostalCode { get; set; }

    public string? Country { get; set; }

    /// <summary>Null when the client has stated no preference.</summary>
    public ContactMethod? PreferredContactMethod { get; set; }

    public string? Notes { get; set; }

    public ClientStatus Status { get; set; } = ClientStatus.Active;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

public enum ClientStatus
{
    Active,
    Inactive,
}

/// <summary>How a client prefers to be contacted. Stored by name, so never rename a member without a data migration.</summary>
public enum ContactMethod
{
    Email,
    Phone,
    Sms,
}

internal sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    public void Configure(EntityTypeBuilder<Client> builder)
    {
        builder.ToTable("clients", table =>
        {
            table.HasCheckConstraint("ck_clients_status", "status IN ('Active', 'Inactive')");
            table.HasCheckConstraint("ck_clients_names", "btrim(first_name) <> '' AND btrim(last_name) <> ''");
            table.HasCheckConstraint("ck_clients_email_lowercase", "email = lower(email)");
            table.HasCheckConstraint(
                "ck_clients_preferred_contact_method", "preferred_contact_method IN ('Email', 'Phone', 'Sms')");
        });
        builder.Property(client => client.FirstName).HasMaxLength(Client.NameMaxLength);
        builder.Property(client => client.LastName).HasMaxLength(Client.NameMaxLength);
        builder.Property(client => client.Email).HasMaxLength(Client.EmailMaxLength);
        builder.Property(client => client.Phone).HasMaxLength(Client.PhoneMaxLength);
        builder.Property(client => client.AddressLine1).HasMaxLength(Client.AddressLineMaxLength);
        builder.Property(client => client.AddressLine2).HasMaxLength(Client.AddressLineMaxLength);
        builder.Property(client => client.City).HasMaxLength(Client.LocalityMaxLength);
        builder.Property(client => client.Region).HasMaxLength(Client.LocalityMaxLength);
        builder.Property(client => client.PostalCode).HasMaxLength(Client.PostalCodeMaxLength);
        builder.Property(client => client.Country).HasMaxLength(Client.LocalityMaxLength);
        builder.Property(client => client.PreferredContactMethod).HasConversion<string>().HasMaxLength(32);
        builder.Property(client => client.Notes).HasMaxLength(Client.NotesMaxLength);
        builder.Property(client => client.Status).HasConversion<string>().HasMaxLength(32);

        // Unique (clinic_id, id): later tables (patients, appointments, invoices) reference clients through a
        // composite foreign key on (clinic_id, client_id), so the database itself refuses a cross-clinic link (ADR-005).
        builder.HasAlternateKey(client => new { client.ClinicId, client.Id });

        // Every list query is scoped to one clinic (NFR-PERF-2); one index per sort, ending in the id tie-breaker.
        // Substring search scans the clinic's rows; a trigram index can be added if X-04 needs it (ADR-005).
        builder.HasIndex(client => new { client.ClinicId, client.LastName, client.FirstName, client.Id });
        builder.HasIndex(client => new { client.ClinicId, client.CreatedAt, client.Id });
        builder.HasIndex(client => new { client.ClinicId, client.UpdatedAt, client.Id });

        // Restrict: clinics are never deleted out from under their clients.
        builder.HasOne<Clinic>()
            .WithMany()
            .HasForeignKey(client => client.ClinicId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
