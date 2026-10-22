using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Clients;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;
using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Features.Appointments;

/// <summary>
/// A booked visit of a patient with a veterinarian (docs/DATABASE.md section 5, ADR-009). Every reference is to a row
/// of the same clinic (composite foreign keys, ADR-005). Never deleted: B-10 cancels or marks no-shows instead.
/// </summary>
public sealed class Appointment : Entity, IClinicOwned
{
    public const int ReasonMaxLength = 500;
    public const int NotesMaxLength = 4000;
    public const int MinDurationMinutes = ScheduleRules.GranularityMinutes;
    public const int MaxDurationMinutes = 12 * 60;

    /// <summary>Name of the exclusion constraint that forbids overlapping un-overridden bookings of one veterinarian.</summary>
    public const string NoOverlapConstraint = "ex_appointments_veterinarian_no_overlap";

    public Guid ClinicId { get; set; }

    public Guid PatientId { get; set; }

    public Patient? Patient { get; set; }

    /// <summary>The patient's owner when the appointment was booked (or moved to another patient); set by the server.</summary>
    public Guid ClientId { get; set; }

    public Client? Client { get; set; }

    public Guid VeterinarianId { get; set; }

    public User? Veterinarian { get; set; }

    public DateTimeOffset StartAt { get; set; }

    public DateTimeOffset EndAt { get; set; }

    public AppointmentType Type { get; set; }

    public string? Reason { get; set; }

    public string? Notes { get; set; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;

    /// <summary>
    /// The conflicts accepted when the appointment was last placed (empty when it was booked cleanly). Non-empty
    /// exactly when <see cref="OverrideReason"/> is set.
    /// </summary>
    public List<AppointmentConflict> OverriddenConflicts { get; set; } = [];

    public string? OverrideReason { get; set; }

    /// <summary>Null only if the creating account could not be recorded (never through the API).</summary>
    public Guid? CreatedByUserId { get; set; }

    public User? CreatedBy { get; set; }

    /// <summary>
    /// Statuses that occupy the veterinarian's and the patient's time. Keep in step with the WHERE clause of
    /// <see cref="NoOverlapConstraint"/> (raw SQL in the AddAppointments migration; a test compares them).
    /// </summary>
    public static readonly AppointmentStatus[] ActiveStatuses = [AppointmentStatus.Scheduled, AppointmentStatus.CheckedIn];
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum AppointmentType
{
    Consultation,
    Vaccination,
    FollowUp,
    Surgery,
    Dental,
    Grooming,
    Emergency,
    Other,
}

/// <summary>The lifecycle (transitions arrive in B-10). B-09 only creates <see cref="Scheduled"/> appointments.</summary>
public enum AppointmentStatus
{
    Scheduled,
    CheckedIn,
    Completed,
    Cancelled,
    NoShow,
}

/// <summary>Why a booking clashes. Each maps to the request field it concerns in a 409 response.</summary>
public enum AppointmentConflict
{
    /// <summary>Outside the veterinarian's working hours, or inside an exception or clinic closure.</summary>
    OutsideWorkingHours,

    /// <summary>Overlaps another active appointment of the same veterinarian.</summary>
    VeterinarianDoubleBooked,

    /// <summary>Overlaps another active appointment of the same patient.</summary>
    PatientDoubleBooked,
}

internal sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> builder)
    {
        builder.ToTable("appointments", table =>
        {
            table.HasCheckConstraint("ck_appointments_type", Names<AppointmentType>("type"));
            table.HasCheckConstraint("ck_appointments_status", Names<AppointmentStatus>("status"));
            table.HasCheckConstraint(
                "ck_appointments_duration",
                $"end_at >= start_at + interval '{Appointment.MinDurationMinutes} minutes' " +
                $"AND end_at <= start_at + interval '{Appointment.MaxDurationMinutes} minutes'");
            table.HasCheckConstraint(
                "ck_appointments_granularity",
                $"extract(epoch FROM start_at) % {ScheduleRules.GranularityMinutes * 60} = 0 " +
                $"AND extract(epoch FROM end_at) % {ScheduleRules.GranularityMinutes * 60} = 0");
            table.HasCheckConstraint(
                "ck_appointments_override",
                "(override_reason IS NULL) = (cardinality(overridden_conflicts) = 0)");
            table.HasCheckConstraint(
                "ck_appointments_overridden_conflicts",
                $"overridden_conflicts <@ ARRAY[{string.Join(", ", Enum.GetNames<AppointmentConflict>().Select(n => $"'{n}'"))}]::text[]");
        });
        builder.Property(a => a.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.Reason).HasMaxLength(Appointment.ReasonMaxLength);
        builder.Property(a => a.Notes).HasMaxLength(Appointment.NotesMaxLength);
        builder.Property(a => a.OverrideReason).HasMaxLength(Appointment.ReasonMaxLength);

        // text[] of conflict names; a primitive collection, so EF stores the enum names directly.
        builder.PrimitiveCollection(a => a.OverriddenConflicts)
            .HasColumnType("text[]")
            .ElementType(element => element.HasConversion<string>());

        // Every reference stays inside the clinic (ADR-005). Restrict: none of these is ever deleted.
        builder.HasOne(a => a.Patient).WithMany()
            .HasForeignKey(a => new { a.ClinicId, a.PatientId })
            .HasPrincipalKey(patient => new { patient.ClinicId, patient.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(a => a.Client).WithMany()
            .HasForeignKey(a => new { a.ClinicId, a.ClientId })
            .HasPrincipalKey(client => new { client.ClinicId, client.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(a => a.Veterinarian).WithMany()
            .HasForeignKey(a => new { a.ClinicId, a.VeterinarianId })
            .HasPrincipalKey(user => new { user.ClinicId, user.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(a => a.CreatedBy).WithMany()
            .HasForeignKey(a => new { a.ClinicId, a.CreatedByUserId })
            .HasPrincipalKey(user => new { user.ClinicId, user.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(a => a.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // Unique (clinic_id, id) so consultations (B-11) and invoices can reference appointments by composite key.
        builder.HasAlternateKey(a => new { a.ClinicId, a.Id });

        // Calendar and conflict queries: by time, by veterinarian and time, by patient and time, by client.
        builder.HasIndex(a => new { a.ClinicId, a.StartAt, a.Id });
        builder.HasIndex(a => new { a.ClinicId, a.VeterinarianId, a.StartAt });
        builder.HasIndex(a => new { a.ClinicId, a.PatientId, a.StartAt });
        builder.HasIndex(a => new { a.ClinicId, a.ClientId, a.StartAt });
    }

    private static string Names<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(name => $"'{name}'"))})";
}
