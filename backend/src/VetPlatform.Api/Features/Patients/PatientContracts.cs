using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;

namespace VetPlatform.Api.Features.Patients;

/// <summary>The owner as embedded in a patient: enough to show and link to, nothing more.</summary>
public sealed record PatientClientSummary(Guid Id, string FirstName, string LastName);

/// <summary>A patient as returned by the patients API (docs/API.md section 6).</summary>
public sealed record PatientResponse(
    Guid Id,
    string Name,
    Species Species,
    string? Breed,
    Sex Sex,
    ReproductiveStatus ReproductiveStatus,
    DateOnly? DateOfBirth,
    bool DateOfBirthEstimated,
    string? Markings,
    string? MicrochipNumber,
    decimal? WeightKg,
    PatientStatus Status,
    DateOnly? DateOfDeath,
    string? Notes,
    PatientClientSummary Client,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The patient's <see cref="Patient.Client"/> must be loaded.</summary>
    public static PatientResponse From(Patient patient)
    {
        var client = patient.Client ?? throw new InvalidOperationException("The patient's client was not loaded.");
        return new(
            patient.Id, patient.Name, patient.Species, patient.Breed, patient.Sex, patient.ReproductiveStatus,
            patient.DateOfBirth, patient.DateOfBirthEstimated, patient.Markings, patient.MicrochipNumber,
            Shortest(patient.WeightKg), patient.Status, patient.DateOfDeath, patient.Notes,
            new PatientClientSummary(client.Id, client.FirstName, client.LastName),
            patient.CreatedAt, patient.UpdatedAt);
    }

    /// <summary>
    /// Drops trailing zeros (31.250 → 31.25). PostgreSQL returns numeric(7,3) with three decimals while a just-saved
    /// value keeps the caller's scale; without this a create response and a later read would differ.
    /// </summary>
    private static decimal? Shortest(decimal? value) => value / 1.000000000000000000000000000000000m;
}

public sealed class PatientListQuery : PageQuery, IValidatableObject
{
    public static readonly string[] SortFields = ["name", "createdAt", "updatedAt"];

    /// <summary>
    /// Matches the patient's name, the owner's first, last or full name (case-insensitive substring), or the
    /// microchip number (substring, ignoring spaces, dashes and dots).
    /// </summary>
    [MaxLength(100)]
    public string? Q { get; init; }

    /// <summary>Only this owner's patients.</summary>
    public Guid? ClientId { get; init; }

    /// <summary>A species name. A string so only exact names are accepted (see <see cref="StrictEnum"/>).</summary>
    public string? Species { get; init; }

    /// <summary><c>Active</c>, <c>Inactive</c> or <c>Deceased</c>. A string so only exact names are accepted.</summary>
    public string? Status { get; init; }

    public Species? ParsedSpecies =>
        StrictEnum.TryParse<Species>(Species, out var species) ? species : null;

    public PatientStatus? ParsedStatus => StrictEnum.TryParse<PatientStatus>(Status, out var status) ? status : null;

    /// <summary><c>name</c> (default), <c>createdAt</c> or <c>updatedAt</c>; prefix with one <c>-</c> for descending.</summary>
    public string Sort { get; init; } = "name";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Species is not null && ParsedSpecies is null)
        {
            yield return new ValidationResult(
                $"Species must be one of {StrictEnum.Names<Species>()}.", [nameof(Species)]);
        }

        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<PatientStatus>()}.", [nameof(Status)]);
        }

        if (!SortFields.Contains(SortSpec.Parse(Sort).Field, StringComparer.Ordinal))
        {
            yield return new ValidationResult(
                $"Sort must be one of {string.Join(", ", SortFields)}, optionally prefixed with '-'.", [nameof(Sort)]);
        }
    }
}

/// <summary>
/// Optional text fields may be omitted, null or blank; all three store nothing. Rules that need the clock or the
/// database (dates, microchip, owner) are checked by <see cref="PatientService"/>.
/// </summary>
public sealed record CreatePatientRequest(
    [Required] Guid? ClientId,
    [Required, MaxLength(Patient.NameMaxLength)] string? Name,
    [Required] Species? Species,
    [MaxLength(Patient.BreedMaxLength)] string? Breed,
    Sex? Sex,
    ReproductiveStatus? ReproductiveStatus,
    DateOnly? DateOfBirth,
    bool? DateOfBirthEstimated,
    [MaxLength(Patient.MarkingsMaxLength)] string? Markings,
    [MaxLength(PatientRequestLimits.MicrochipInputMaxLength)] string? MicrochipNumber,
    decimal? WeightKg,
    [MaxLength(Patient.NotesMaxLength)] string? Notes);

/// <summary>
/// Partial update: omitted or null fields are left unchanged and <c>""</c> clears an optional text field.
/// <c>dateOfBirth</c>, <c>weightKg</c> and <c>dateOfDeath</c> are cleared with an explicit <c>null</c>.
/// </summary>
public sealed record UpdatePatientRequest(
    Guid? ClientId,
    [MaxLength(Patient.NameMaxLength)] string? Name,
    Species? Species,
    [MaxLength(Patient.BreedMaxLength)] string? Breed,
    Sex? Sex,
    ReproductiveStatus? ReproductiveStatus,
    Optional<DateOnly?> DateOfBirth,
    bool? DateOfBirthEstimated,
    [MaxLength(Patient.MarkingsMaxLength)] string? Markings,
    [MaxLength(PatientRequestLimits.MicrochipInputMaxLength)] string? MicrochipNumber,
    Optional<decimal?> WeightKg,
    PatientStatus? Status,
    Optional<DateOnly?> DateOfDeath,
    [MaxLength(Patient.NotesMaxLength)] string? Notes) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Name is not null && string.IsNullOrWhiteSpace(Name))
        {
            yield return new ValidationResult("The name must not be blank.", [nameof(Name)]);
        }
    }
}

public static class PatientRequestLimits
{
    /// <summary>Room for separators ("985 112-003.456789"); the normalised number is at most 20 characters.</summary>
    public const int MicrochipInputMaxLength = 40;
}
