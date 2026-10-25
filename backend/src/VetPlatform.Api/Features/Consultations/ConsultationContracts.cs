using System.ComponentModel.DataAnnotations;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Features.Appointments;

namespace VetPlatform.Api.Features.Consultations;

public sealed record ConsultationVitals(
    decimal? WeightKg,
    decimal? TemperatureC,
    int? HeartRate,
    int? RespiratoryRate,
    int? BodyConditionScore,
    int? PainScore);

public sealed record DiagnosisResponse(
    Guid Id,
    string Name,
    string? Code,
    string? CodeSystem,
    DiagnosisStatus Status,
    bool IsPrimary,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static DiagnosisResponse From(Diagnosis d) =>
        new(d.Id, d.Name, d.Code, d.CodeSystem, d.Status, d.IsPrimary, d.Notes, d.CreatedAt, d.UpdatedAt);
}

public sealed record TreatmentResponse(
    Guid Id,
    string Name,
    TreatmentType Type,
    Guid? DiagnosisId,
    string? Dose,
    string? Route,
    string? Frequency,
    string? Duration,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static TreatmentResponse From(Treatment t) =>
        new(t.Id, t.Name, t.Type, t.DiagnosisId, t.Dose, t.Route, t.Frequency, t.Duration, t.Notes, t.CreatedAt, t.UpdatedAt);
}

/// <summary>A prescription (docs/API.md section 9). <c>cancelledAt</c> carries the clinic's offset.</summary>
public sealed record PrescriptionResponse(
    Guid Id,
    string MedicationName,
    string Dose,
    string? Route,
    string Frequency,
    string? Duration,
    decimal Quantity,
    string QuantityUnit,
    int Refills,
    string? Instructions,
    Guid? DiagnosisId,
    PrescriptionStatus Status,
    AppointmentPersonSummary? PrescribedBy,
    DateTimeOffset? CancelledAt,
    AppointmentPersonSummary? CancelledBy,
    string? CancelReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The prescriber and canceller must be loaded (a missing one, e.g. another clinic's, shows null).</summary>
    public static PrescriptionResponse From(Prescription p, TimeZoneInfo zone) =>
        new(
            p.Id, p.MedicationName, p.Dose, p.Route, p.Frequency, p.Duration,
            p.Quantity / 1.000000000000000000000000000000000m, // numeric(10,3) returns 14.000; respond with 14
            p.QuantityUnit, p.Refills, p.Instructions, p.DiagnosisId, p.Status,
            p.PrescribedBy is { } by ? new AppointmentPersonSummary(by.Id, by.Name) : null,
            p.CancelledAt is { } at ? TimeZoneInfo.ConvertTime(at, zone) : null,
            p.CancelledBy is { } canceller ? new AppointmentPersonSummary(canceller.Id, canceller.Name) : null,
            p.CancelReason, p.CreatedAt, p.UpdatedAt);
}

public sealed record ConsultationAddendumResponse(Guid Id, string Text, AppointmentPersonSummary? Author, DateTimeOffset CreatedAt);

/// <summary>A consultation as returned by the API (docs/API.md section 8). Clinical instants carry the clinic's offset.</summary>
public sealed record ConsultationResponse(
    Guid Id,
    AppointmentPatientSummary Patient,
    Guid? AppointmentId,
    AppointmentPersonSummary Veterinarian,
    ConsultationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    AppointmentPersonSummary? CompletedBy,
    DateTimeOffset? DiscardedAt,
    string? DiscardReason,
    string? Subjective,
    string? Objective,
    string? Assessment,
    string? Plan,
    ConsultationVitals Vitals,
    IReadOnlyList<DiagnosisResponse> Diagnoses,
    IReadOnlyList<TreatmentResponse> Treatments,
    IReadOnlyList<PrescriptionResponse> Prescriptions,
    IReadOnlyList<ConsultationAddendumResponse> Addenda,
    AppointmentPersonSummary? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Patient, veterinarian, completer, creator and addenda (with authors) must be loaded.</summary>
    public static ConsultationResponse From(Consultation c, TimeZoneInfo zone)
    {
        var patient = c.Patient ?? throw new InvalidOperationException("The patient was not loaded.");
        var vet = c.Veterinarian ?? throw new InvalidOperationException("The veterinarian was not loaded.");
        return new(
            c.Id,
            new AppointmentPatientSummary(patient.Id, patient.Name, patient.Species),
            c.AppointmentId,
            new AppointmentPersonSummary(vet.Id, vet.Name),
            c.Status,
            TimeZoneInfo.ConvertTime(c.StartedAt, zone),
            Local(c.CompletedAt, zone),
            Person(c.CompletedBy),
            Local(c.DiscardedAt, zone),
            c.DiscardReason,
            c.Subjective,
            c.Objective,
            c.Assessment,
            c.Plan,
            new ConsultationVitals(
                Shortest(c.WeightKg), Shortest(c.TemperatureC), c.HeartRate, c.RespiratoryRate, c.BodyConditionScore, c.PainScore),
            // Primary first, then in the order recorded.
            c.Diagnoses.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.CreatedAt).ThenBy(d => d.Id)
                .Select(DiagnosisResponse.From).ToList(),
            c.Treatments.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Select(TreatmentResponse.From).ToList(),
            c.Prescriptions.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).Select(p => PrescriptionResponse.From(p, zone)).ToList(),
            c.Addenda.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .Select(a => new ConsultationAddendumResponse(a.Id, a.Text, Person(a.Author), TimeZoneInfo.ConvertTime(a.CreatedAt, zone)))
                .ToList(),
            Person(c.CreatedBy),
            c.CreatedAt,
            c.UpdatedAt);
    }

    private static AppointmentPersonSummary? Person(Identity.User? user) =>
        user is null ? null : new AppointmentPersonSummary(user.Id, user.Name);

    private static DateTimeOffset? Local(DateTimeOffset? instant, TimeZoneInfo zone) =>
        instant is { } value ? TimeZoneInfo.ConvertTime(value, zone) : null;

    /// <summary>numeric columns come back with their full scale (31.250); responses use the shortest form.</summary>
    private static decimal? Shortest(decimal? value) => value / 1.000000000000000000000000000000000m;
}

/// <summary>
/// Opens a consultation. <c>appointmentId</c> (optional) must be a CheckedIn appointment of the same patient;
/// <c>veterinarianId</c> must be an active veterinarian of the clinic.
/// </summary>
public sealed record CreateConsultationRequest(
    [Required] Guid? PatientId,
    [Required] Guid? VeterinarianId,
    Guid? AppointmentId,
    [MaxLength(Consultation.NotesMaxLength)] string? Subjective,
    [MaxLength(Consultation.NotesMaxLength)] string? Objective,
    [MaxLength(Consultation.NotesMaxLength)] string? Assessment,
    [MaxLength(Consultation.NotesMaxLength)] string? Plan,
    decimal? WeightKg,
    decimal? TemperatureC,
    int? HeartRate,
    int? RespiratoryRate,
    int? BodyConditionScore,
    int? PainScore);

/// <summary>
/// Edits an in-progress consultation. Omitted or null fields are unchanged; <c>""</c> clears a note. Each vital is an
/// <see cref="Optional{T}"/>: omitted leaves it, explicit null clears it.
/// </summary>
public sealed record UpdateConsultationRequest(
    Guid? VeterinarianId,
    [MaxLength(Consultation.NotesMaxLength)] string? Subjective,
    [MaxLength(Consultation.NotesMaxLength)] string? Objective,
    [MaxLength(Consultation.NotesMaxLength)] string? Assessment,
    [MaxLength(Consultation.NotesMaxLength)] string? Plan,
    Optional<decimal?> WeightKg,
    Optional<decimal?> TemperatureC,
    Optional<int?> HeartRate,
    Optional<int?> RespiratoryRate,
    Optional<int?> BodyConditionScore,
    Optional<int?> PainScore);

public sealed record DiscardConsultationRequest([Required, MaxLength(Consultation.ReasonMaxLength)] string? Reason);

public sealed record AddAddendumRequest([Required, MaxLength(ConsultationAddendum.TextMaxLength)] string? Text);

public sealed class ConsultationListQuery : PageQuery, IValidatableObject
{
    /// <summary>InProgress, Completed or Discarded. Without it, discarded consultations are left out.</summary>
    public string? Status { get; init; }

    public ConsultationStatus? ParsedStatus =>
        StrictEnum.TryParse<ConsultationStatus>(Status, out var status) ? status : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult(
                $"Status must be one of {StrictEnum.Names<ConsultationStatus>()}.", [nameof(Status)]);
        }
    }
}

/// <summary>Optional text fields may be omitted, null or blank; all three store nothing. Defaults: Provisional, not primary.</summary>
public sealed record CreateDiagnosisRequest(
    [Required, MaxLength(Diagnosis.NameMaxLength)] string? Name,
    [MaxLength(Diagnosis.CodeMaxLength)] string? Code,
    [MaxLength(Diagnosis.CodeMaxLength)] string? CodeSystem,
    DiagnosisStatus? Status,
    bool? IsPrimary,
    [MaxLength(Diagnosis.NotesMaxLength)] string? Notes);

/// <summary>
/// Partial update; omitted or null fields are unchanged and <c>""</c> clears optional text. After the consultation is
/// completed only <c>status</c> may be sent (veterinarians only).
/// </summary>
public sealed record UpdateDiagnosisRequest(
    [MaxLength(Diagnosis.NameMaxLength)] string? Name,
    [MaxLength(Diagnosis.CodeMaxLength)] string? Code,
    [MaxLength(Diagnosis.CodeMaxLength)] string? CodeSystem,
    DiagnosisStatus? Status,
    bool? IsPrimary,
    [MaxLength(Diagnosis.NotesMaxLength)] string? Notes);

public sealed record CreateTreatmentRequest(
    [Required, MaxLength(Treatment.NameMaxLength)] string? Name,
    [Required] TreatmentType? Type,
    Guid? DiagnosisId,
    [MaxLength(Treatment.DetailMaxLength)] string? Dose,
    [MaxLength(Treatment.DetailMaxLength)] string? Route,
    [MaxLength(Treatment.DetailMaxLength)] string? Frequency,
    [MaxLength(Treatment.DetailMaxLength)] string? Duration,
    [MaxLength(Treatment.NotesMaxLength)] string? Notes);

/// <summary>Partial update; <c>""</c> clears optional text; <c>diagnosisId</c> is cleared with an explicit null.</summary>
public sealed record UpdateTreatmentRequest(
    [MaxLength(Treatment.NameMaxLength)] string? Name,
    TreatmentType? Type,
    Optional<Guid?> DiagnosisId,
    [MaxLength(Treatment.DetailMaxLength)] string? Dose,
    [MaxLength(Treatment.DetailMaxLength)] string? Route,
    [MaxLength(Treatment.DetailMaxLength)] string? Frequency,
    [MaxLength(Treatment.DetailMaxLength)] string? Duration,
    [MaxLength(Treatment.NotesMaxLength)] string? Notes);

/// <summary>A diagnosis in the patient's problem list, with the consultation it came from.</summary>
public sealed record PatientDiagnosisResponse(
    DiagnosisResponse Diagnosis,
    Guid ConsultationId,
    DateTimeOffset ConsultationCompletedAt,
    AppointmentPersonSummary Veterinarian);

public sealed class PatientDiagnosisListQuery : PageQuery, IValidatableObject
{
    public string? Status { get; init; }

    public DiagnosisStatus? ParsedStatus => StrictEnum.TryParse<DiagnosisStatus>(Status, out var status) ? status : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<DiagnosisStatus>()}.", [nameof(Status)]);
        }
    }
}

/// <summary>
/// Adds a prescription. <c>medicationName</c>, <c>dose</c>, <c>frequency</c>, <c>quantity</c> and <c>quantityUnit</c> are
/// required; optional text may be omitted, null or blank (stores nothing). <c>refills</c> defaults to 0.
/// </summary>
public sealed record CreatePrescriptionRequest(
    [Required, MaxLength(Prescription.MedicationNameMaxLength)] string? MedicationName,
    [Required, MaxLength(Prescription.DetailMaxLength)] string? Dose,
    [MaxLength(Prescription.DetailMaxLength)] string? Route,
    [Required, MaxLength(Prescription.DetailMaxLength)] string? Frequency,
    [MaxLength(Prescription.DetailMaxLength)] string? Duration,
    [Required] decimal? Quantity,
    [Required, MaxLength(Prescription.QuantityUnitMaxLength)] string? QuantityUnit,
    int? Refills,
    [MaxLength(Prescription.InstructionsMaxLength)] string? Instructions,
    Guid? DiagnosisId);

/// <summary>
/// Partial update of a prescription of an in-progress consultation; omitted or null fields are unchanged, <c>""</c>
/// clears optional text (required text cannot be blank), and <c>diagnosisId</c> is cleared with an explicit null.
/// </summary>
public sealed record UpdatePrescriptionRequest(
    [MaxLength(Prescription.MedicationNameMaxLength)] string? MedicationName,
    [MaxLength(Prescription.DetailMaxLength)] string? Dose,
    [MaxLength(Prescription.DetailMaxLength)] string? Route,
    [MaxLength(Prescription.DetailMaxLength)] string? Frequency,
    [MaxLength(Prescription.DetailMaxLength)] string? Duration,
    decimal? Quantity,
    [MaxLength(Prescription.QuantityUnitMaxLength)] string? QuantityUnit,
    int? Refills,
    [MaxLength(Prescription.InstructionsMaxLength)] string? Instructions,
    Optional<Guid?> DiagnosisId);

public sealed record CancelPrescriptionRequest([Required, MaxLength(Prescription.ReasonMaxLength)] string? Reason);

/// <summary>A prescription in the patient's medication list, with the consultation it came from.</summary>
public sealed record PatientPrescriptionResponse(
    PrescriptionResponse Prescription,
    Guid ConsultationId,
    DateTimeOffset ConsultationCompletedAt,
    AppointmentPersonSummary Veterinarian);

public sealed class PatientPrescriptionListQuery : PageQuery, IValidatableObject
{
    public string? Status { get; init; }

    public PrescriptionStatus? ParsedStatus =>
        StrictEnum.TryParse<PrescriptionStatus>(Status, out var status) ? status : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is not null && ParsedStatus is null)
        {
            yield return new ValidationResult($"Status must be one of {StrictEnum.Names<PrescriptionStatus>()}.", [nameof(Status)]);
        }
    }
}
