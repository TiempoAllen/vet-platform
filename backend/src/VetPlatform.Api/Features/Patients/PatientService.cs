using Microsoft.EntityFrameworkCore;
using Npgsql;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Clients;

namespace VetPlatform.Api.Features.Patients;

/// <summary>
/// Patient records within the caller's clinic (docs/API.md section 6). Every query runs under the clinic query
/// filter, so another clinic's patients are simply not found (404), and another clinic's client cannot be chosen as
/// owner (and the composite foreign key would refuse it anyway, ADR-005). Changes and their audit entries commit
/// together. Patients are never deleted (FR-CLI-3 applies to them too).
/// </summary>
/// <remarks>
/// Rules needing the clock or the database are checked here and reported together as one 422; audit metadata names
/// changed fields and owner ids, never names or notes.
/// </remarks>
public sealed class PatientService(AppDbContext db, ITenantContext tenant, AuditTrail audit, TimeProvider timeProvider)
{
    public Task<PagedResponse<PatientResponse>> ListAsync(PatientListQuery query, CancellationToken cancellationToken)
    {
        var patients = db.Patients.AsNoTracking().Include(patient => patient.Client).AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var term = query.Q.Trim();
            var pattern = PagingExtensions.ContainsPattern(term);
            var chip = Patient.NormalizeMicrochip(term);
            var chipPattern = string.IsNullOrEmpty(chip) ? null : PagingExtensions.ContainsPattern(chip);
            patients = patients.Where(patient =>
                EF.Functions.ILike(patient.Name, pattern, PagingExtensions.LikeEscape)
                || EF.Functions.ILike(patient.Client!.FirstName, pattern, PagingExtensions.LikeEscape)
                || EF.Functions.ILike(patient.Client!.LastName, pattern, PagingExtensions.LikeEscape)
                || EF.Functions.ILike(
                    patient.Client!.FirstName + " " + patient.Client!.LastName, pattern, PagingExtensions.LikeEscape)
                || (chipPattern != null
                    && EF.Functions.Like(patient.MicrochipNumber!, chipPattern, PagingExtensions.LikeEscape)));
        }

        if (query.ClientId is { } clientId)
        {
            patients = patients.Where(patient => patient.ClientId == clientId);
        }

        if (query.ParsedSpecies is { } species)
        {
            patients = patients.Where(patient => patient.Species == species);
        }

        if (query.ParsedStatus is { } status)
        {
            patients = patients.Where(patient => patient.Status == status);
        }

        var (field, descending) = SortSpec.Parse(query.Sort);
        var ordered = field switch
        {
            "createdAt" => descending ? patients.OrderByDescending(p => p.CreatedAt) : patients.OrderBy(p => p.CreatedAt),
            "updatedAt" => descending ? patients.OrderByDescending(p => p.UpdatedAt) : patients.OrderBy(p => p.UpdatedAt),
            _ => descending ? patients.OrderByDescending(p => p.Name) : patients.OrderBy(p => p.Name),
        };

        // Id as a tie-breaker, in the sort direction, keeps pages stable and lets the indexes serve both directions.
        return (descending ? ordered.ThenByDescending(p => p.Id) : ordered.ThenBy(p => p.Id))
            .ToPageAsync(query, PatientResponse.From, cancellationToken);
    }

    public async Task<PatientResponse> GetAsync(Guid id, CancellationToken cancellationToken) =>
        PatientResponse.From(await FindAsync(id, tracking: false, cancellationToken));

    public async Task<PatientResponse> CreateAsync(CreatePatientRequest request, CancellationToken cancellationToken)
    {
        var problems = new List<ApiErrorDetail>();
        var owner = await FindActiveOwnerAsync(request.ClientId!.Value, problems, cancellationToken);
        var microchip = CheckMicrochip(request.MicrochipNumber, problems);
        CheckWeight(request.WeightKg, problems);
        CheckDateOfBirth(request.DateOfBirth, problems);
        ThrowIfAny(problems);

        if (microchip is not null)
        {
            await EnsureMicrochipIsFreeAsync(microchip, exceptPatientId: null, cancellationToken);
        }

        var patient = new Patient
        {
            ClinicId = CurrentClinicId,
            ClientId = owner!.Id,
            Client = owner,
            Name = request.Name!.Trim(),
            Species = request.Species!.Value,
            Breed = NullIfBlank(request.Breed),
            Sex = request.Sex ?? Sex.Unknown,
            ReproductiveStatus = request.ReproductiveStatus ?? ReproductiveStatus.Unknown,
            DateOfBirth = request.DateOfBirth,
            DateOfBirthEstimated = request.DateOfBirth is not null && request.DateOfBirthEstimated == true,
            Markings = NullIfBlank(request.Markings),
            MicrochipNumber = microchip,
            WeightKg = request.WeightKg,
            Notes = NullIfBlank(request.Notes),
        };
        db.Patients.Add(patient);
        audit.Record(AuditActions.PatientCreated, nameof(Patient), patient.Id, new { clientId = owner.Id });

        await SaveAsync(cancellationToken);
        return PatientResponse.From(patient);
    }

    public async Task<PatientResponse> UpdateAsync(Guid id, UpdatePatientRequest request, CancellationToken cancellationToken)
    {
        var patient = await FindAsync(id, tracking: true, cancellationToken);
        var changed = new List<string>();
        var problems = new List<ApiErrorDetail>();
        var previousClientId = patient.ClientId;

        void Set<T>(string field, T current, T wanted, Action<T> apply)
        {
            if (!EqualityComparer<T>.Default.Equals(current, wanted))
            {
                apply(wanted);
                changed.Add(field);
            }
        }

        void SetText(string field, string? requested, string? current, Action<string?> apply)
        {
            if (requested is not null)
            {
                Set(field, current, NullIfBlank(requested), apply);
            }
        }

        // Transfer: only to an active client of this clinic. Keeping the current owner is always allowed, even if
        // that client has since been deactivated.
        if (request.ClientId is { } clientId && clientId != patient.ClientId)
        {
            if (await FindActiveOwnerAsync(clientId, problems, cancellationToken) is { } owner)
            {
                patient.ClientId = owner.Id;
                patient.Client = owner;
                changed.Add("clientId");
            }
        }

        if (request.Name is { } name)
        {
            Set("name", patient.Name, name.Trim(), value => patient.Name = value);
        }

        if (request.Species is { } species)
        {
            Set("species", patient.Species, species, value => patient.Species = value);
        }

        SetText("breed", request.Breed, patient.Breed, value => patient.Breed = value);

        if (request.Sex is { } sex)
        {
            Set("sex", patient.Sex, sex, value => patient.Sex = value);
        }

        if (request.ReproductiveStatus is { } reproductiveStatus)
        {
            Set("reproductiveStatus", patient.ReproductiveStatus, reproductiveStatus, value => patient.ReproductiveStatus = value);
        }

        if (request.DateOfBirth.IsSet)
        {
            CheckDateOfBirth(request.DateOfBirth.Value, problems);
            Set("dateOfBirth", patient.DateOfBirth, request.DateOfBirth.Value, value => patient.DateOfBirth = value);
        }

        // An estimate flag without a date means nothing, so it is false whenever there is no date.
        var estimated = patient.DateOfBirth is not null && (request.DateOfBirthEstimated ?? patient.DateOfBirthEstimated);
        Set("dateOfBirthEstimated", patient.DateOfBirthEstimated, estimated, value => patient.DateOfBirthEstimated = value);

        SetText("markings", request.Markings, patient.Markings, value => patient.Markings = value);

        var microchipToCheck = (string?)null;
        if (request.MicrochipNumber is not null)
        {
            var microchip = CheckMicrochip(request.MicrochipNumber, problems);
            if (microchip != patient.MicrochipNumber && microchip is not null)
            {
                microchipToCheck = microchip;
            }

            Set("microchipNumber", patient.MicrochipNumber, microchip, value => patient.MicrochipNumber = value);
        }

        if (request.WeightKg.IsSet)
        {
            CheckWeight(request.WeightKg.Value, problems);
            Set("weightKg", patient.WeightKg, request.WeightKg.Value, value => patient.WeightKg = value);
        }

        if (request.Status is { } status)
        {
            Set("status", patient.Status, status, value => patient.Status = value);
        }

        if (request.DateOfDeath.IsSet)
        {
            Set("dateOfDeath", patient.DateOfDeath, request.DateOfDeath.Value, value => patient.DateOfDeath = value);
        }
        else if (patient.Status != PatientStatus.Deceased && patient.DateOfDeath is not null)
        {
            // Leaving "Deceased" (a correction) drops the date of death with it.
            patient.DateOfDeath = null;
            changed.Add("dateOfDeath");
        }

        CheckDateOfDeath(patient, problems);
        SetText("notes", request.Notes, patient.Notes, value => patient.Notes = value);
        ThrowIfAny(problems);

        if (microchipToCheck is not null)
        {
            await EnsureMicrochipIsFreeAsync(microchipToCheck, exceptPatientId: patient.Id, cancellationToken);
        }

        if (changed.Count > 0)
        {
            audit.Record(
                AuditActions.PatientUpdated, nameof(Patient), patient.Id,
                patient.ClientId == previousClientId
                    ? new { fields = changed, status = patient.Status }
                    : new { fields = changed, status = patient.Status, fromClientId = previousClientId, toClientId = patient.ClientId });
            await SaveAsync(cancellationToken);
        }

        return PatientResponse.From(patient);
    }

    private Guid CurrentClinicId =>
        tenant.ClinicId ?? throw new InvalidOperationException("Patient management requires a current clinic.");

    private DateOnly LatestAllowedDate =>
        // Clinics have no timezone yet (B-26), so allow one day ahead of UTC rather than reject a valid local date.
        DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(1);

    private async Task<Patient> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var patients = tracking ? db.Patients : db.Patients.AsNoTracking();
        return await patients.Include(patient => patient.Client)
                   .SingleOrDefaultAsync(patient => patient.Id == id, cancellationToken)
               ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The patient was not found.");
    }

    /// <summary>
    /// The owner must be an active client of the caller's clinic. Unknown, inactive and other clinics' clients get
    /// the same message, so the response reveals nothing about other clinics.
    /// </summary>
    private async Task<Client?> FindActiveOwnerAsync(Guid clientId, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        var owner = await db.Clients.SingleOrDefaultAsync(
            client => client.Id == clientId && client.Status == ClientStatus.Active, cancellationToken);
        if (owner is null)
        {
            problems.Add(new ApiErrorDetail("clientId", "The client was not found or is not active."));
        }

        return owner;
    }

    private static string? CheckMicrochip(string? raw, List<ApiErrorDetail> problems)
    {
        var microchip = Patient.NormalizeMicrochip(raw);
        if (microchip is not null && !Patient.IsValidMicrochip(microchip))
        {
            problems.Add(new ApiErrorDetail(
                "microchipNumber",
                $"The microchip number must be 1 to {Patient.MicrochipMaxLength} letters or digits (spaces, dashes and dots are ignored)."));
        }

        return microchip;
    }

    private static void CheckWeight(decimal? weightKg, List<ApiErrorDetail> problems)
    {
        if (weightKg is { } weight && (weight <= 0 || weight > Patient.MaxWeightKg || decimal.Round(weight, 3) != weight))
        {
            problems.Add(new ApiErrorDetail(
                "weightKg", $"The weight must be more than 0 and at most {Patient.MaxWeightKg} kg, with at most 3 decimals."));
        }
    }

    private void CheckDateOfBirth(DateOnly? dateOfBirth, List<ApiErrorDetail> problems)
    {
        if (dateOfBirth > LatestAllowedDate)
        {
            problems.Add(new ApiErrorDetail("dateOfBirth", "The date of birth cannot be in the future."));
        }
    }

    private void CheckDateOfDeath(Patient patient, List<ApiErrorDetail> problems)
    {
        if (patient.DateOfDeath is not { } dateOfDeath)
        {
            return;
        }

        if (patient.Status != PatientStatus.Deceased)
        {
            problems.Add(new ApiErrorDetail("dateOfDeath", "A date of death needs the status Deceased."));
        }
        else if (dateOfDeath > LatestAllowedDate)
        {
            problems.Add(new ApiErrorDetail("dateOfDeath", "The date of death cannot be in the future."));
        }
        else if (patient.DateOfBirth is { } dateOfBirth && dateOfDeath < dateOfBirth)
        {
            problems.Add(new ApiErrorDetail("dateOfDeath", "The date of death cannot be before the date of birth."));
        }
    }

    private async Task EnsureMicrochipIsFreeAsync(string microchip, Guid? exceptPatientId, CancellationToken cancellationToken)
    {
        if (await db.Patients.AnyAsync(p => p.MicrochipNumber == microchip && p.Id != exceptPatientId, cancellationToken))
        {
            throw MicrochipTaken();
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
                  {
                      SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: MicrochipIndexName,
                  })
        {
            throw MicrochipTaken(); // lost a race with a concurrent create or update using the same chip
        }
    }

    private const string MicrochipIndexName = "ix_patients_clinic_id_microchip_number";

    private static void ThrowIfAny(List<ApiErrorDetail> problems)
    {
        if (problems.Count > 0)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.",
                problems);
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ApiException MicrochipTaken() =>
        new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Another patient already has this microchip number.",
            [new ApiErrorDetail("microchipNumber", "Another patient already has this microchip number.")]);
}
