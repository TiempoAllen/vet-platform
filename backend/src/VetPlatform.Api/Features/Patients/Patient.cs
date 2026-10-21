using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Clients;

namespace VetPlatform.Api.Features.Patients;

/// <summary>
/// An animal under the clinic's care (docs/DATABASE.md section 4). Belongs to one primary client (its owner) of the
/// same clinic, enforced by a composite foreign key (ADR-005). Never deleted: clinical and financial history will
/// refer to it; it is deactivated or marked deceased instead.
/// </summary>
/// <remarks>
/// Multiple responsible owners (FR-PAT-2) can be added later as a <c>patient_owners</c> table beside
/// <see cref="ClientId"/>, which then stays the primary owner.
/// </remarks>
public sealed partial class Patient : Entity, IClinicOwned
{
    public const int NameMaxLength = 100;
    public const int BreedMaxLength = 100;
    public const int MarkingsMaxLength = 200;
    public const int MicrochipMaxLength = 20;
    public const int NotesMaxLength = 4000;
    public const decimal MaxWeightKg = 2000m;

    public Guid ClinicId { get; set; }

    public Guid ClientId { get; set; }

    public Client? Client { get; set; }

    public required string Name { get; set; }

    public Species Species { get; set; }

    public string? Breed { get; set; }

    public Sex Sex { get; set; } = Sex.Unknown;

    public ReproductiveStatus ReproductiveStatus { get; set; } = ReproductiveStatus.Unknown;

    public DateOnly? DateOfBirth { get; set; }

    /// <summary>True when <see cref="DateOfBirth"/> is an estimate (rescues, "about three years old").</summary>
    public bool DateOfBirthEstimated { get; set; }

    /// <summary>Colour and markings, free text.</summary>
    public string? Markings { get; set; }

    /// <summary>Stored normalised (see <see cref="NormalizeMicrochip"/>); unique within the clinic.</summary>
    public string? MicrochipNumber { get; set; }

    /// <summary>Current weight. A dated weight history arrives with consultation vitals (B-11).</summary>
    public decimal? WeightKg { get; set; }

    public PatientStatus Status { get; set; } = PatientStatus.Active;

    /// <summary>Only set while <see cref="Status"/> is <see cref="PatientStatus.Deceased"/>.</summary>
    public DateOnly? DateOfDeath { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Removes spaces, dashes and dots and upper-cases, so "985 112-003.456789" and "985112003456789" are the same
    /// chip. Returns null for blank input.
    /// </summary>
    public static string? NormalizeMicrochip(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return MicrochipSeparators().Replace(value, "").ToUpperInvariant();
    }

    /// <summary>A normalised chip number: letters and digits only.</summary>
    public static bool IsValidMicrochip(string normalized) =>
        normalized.Length is > 0 and <= MicrochipMaxLength && normalized.All(char.IsAsciiLetterOrDigit);

    [GeneratedRegex(@"[\s\-.]")]
    private static partial Regex MicrochipSeparators();
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum Species
{
    Dog,
    Cat,
    Bird,
    Rabbit,
    Rodent,
    Reptile,
    Horse,
    Other,
}

public enum Sex
{
    Male,
    Female,
    Unknown,
}

/// <summary><see cref="Neutered"/> covers both spayed and castrated.</summary>
public enum ReproductiveStatus
{
    Intact,
    Neutered,
    Unknown,
}

public enum PatientStatus
{
    Active,
    Inactive,
    Deceased,
}

internal sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> builder)
    {
        builder.ToTable("patients", table =>
        {
            table.HasCheckConstraint("ck_patients_species", Names<Species>("species"));
            table.HasCheckConstraint("ck_patients_sex", Names<Sex>("sex"));
            table.HasCheckConstraint("ck_patients_reproductive_status", Names<ReproductiveStatus>("reproductive_status"));
            table.HasCheckConstraint("ck_patients_status", Names<PatientStatus>("status"));
            table.HasCheckConstraint("ck_patients_name", "btrim(name) <> ''");
            table.HasCheckConstraint(
                "ck_patients_date_of_birth_estimated", "NOT date_of_birth_estimated OR date_of_birth IS NOT NULL");
            table.HasCheckConstraint("ck_patients_weight_kg", $"weight_kg > 0 AND weight_kg <= {Patient.MaxWeightKg}");
            table.HasCheckConstraint("ck_patients_microchip_number", "microchip_number ~ '^[A-Z0-9]+$'");
            table.HasCheckConstraint(
                "ck_patients_date_of_death",
                "date_of_death IS NULL OR (status = 'Deceased' AND (date_of_birth IS NULL OR date_of_death >= date_of_birth))");
        });
        builder.Property(patient => patient.Name).HasMaxLength(Patient.NameMaxLength);
        builder.Property(patient => patient.Species).HasConversion<string>().HasMaxLength(32);
        builder.Property(patient => patient.Breed).HasMaxLength(Patient.BreedMaxLength);
        builder.Property(patient => patient.Sex).HasConversion<string>().HasMaxLength(32);
        builder.Property(patient => patient.ReproductiveStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(patient => patient.Markings).HasMaxLength(Patient.MarkingsMaxLength);
        builder.Property(patient => patient.MicrochipNumber).HasMaxLength(Patient.MicrochipMaxLength);
        builder.Property(patient => patient.WeightKg).HasPrecision(7, 3);
        builder.Property(patient => patient.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(patient => patient.Notes).HasMaxLength(Patient.NotesMaxLength);

        // Unique (clinic_id, id) so later clinical tables can reference patients with a composite key (ADR-005).
        builder.HasAlternateKey(patient => new { patient.ClinicId, patient.Id });

        // The owner must belong to the same clinic: the database refuses a cross-clinic link (ADR-005). Restrict:
        // clients are never deleted out from under their patients.
        builder.HasOne(patient => patient.Client)
            .WithMany()
            .HasForeignKey(patient => new { patient.ClinicId, patient.ClientId })
            .HasPrincipalKey(client => new { client.ClinicId, client.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // A chip identifies one animal within a clinic. Other clinics may hold the same chip; uniqueness across
        // clinics would reveal that another clinic treats the animal.
        builder.HasIndex(patient => new { patient.ClinicId, patient.MicrochipNumber })
            .IsUnique()
            .HasFilter("microchip_number IS NOT NULL");

        // List queries (NFR-PERF-2): one index per sort, ending in the id tie-breaker, plus "patients of a client".
        builder.HasIndex(patient => new { patient.ClinicId, patient.Name, patient.Id });
        builder.HasIndex(patient => new { patient.ClinicId, patient.CreatedAt, patient.Id });
        builder.HasIndex(patient => new { patient.ClinicId, patient.UpdatedAt, patient.Id });
        builder.HasIndex(patient => new { patient.ClinicId, patient.ClientId, patient.Name, patient.Id });

        // Clinic FK in its own right too, so the clinic cannot be deleted even if the composite key changes later.
        builder.HasOne<Identity.Clinic>()
            .WithMany()
            .HasForeignKey(patient => patient.ClinicId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static string Names<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(name => $"'{name}'"))})";
}
