using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:CollationDefinition:case_insensitive", "und-u-ks-level2,und-u-ks-level2,icu,False")
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .OldAnnotation("Npgsql:CollationDefinition:case_insensitive", "und-u-ks-level2,und-u-ks-level2,icu,False");

            migrationBuilder.CreateTable(
                name: "appointments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    veterinarian_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    overridden_conflicts = table.Column<string[]>(type: "text[]", nullable: false),
                    override_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appointments", x => x.id);
                    table.UniqueConstraint("ak_appointments_clinic_id_id", x => new { x.clinic_id, x.id });
                    table.CheckConstraint("ck_appointments_duration", "end_at >= start_at + interval '5 minutes' AND end_at <= start_at + interval '720 minutes'");
                    table.CheckConstraint("ck_appointments_granularity", "extract(epoch FROM start_at) % 300 = 0 AND extract(epoch FROM end_at) % 300 = 0");
                    table.CheckConstraint("ck_appointments_override", "(override_reason IS NULL) = (cardinality(overridden_conflicts) = 0)");
                    table.CheckConstraint("ck_appointments_status", "status IN ('Scheduled', 'CheckedIn', 'Completed', 'Cancelled', 'NoShow')");
                    table.CheckConstraint("ck_appointments_type", "type IN ('Consultation', 'Vaccination', 'FollowUp', 'Surgery', 'Dental', 'Grooming', 'Emergency', 'Other')");
                    table.ForeignKey(
                        name: "fk_appointments_clients_clinic_id_client_id",
                        columns: x => new { x.clinic_id, x.client_id },
                        principalTable: "clients",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_appointments_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_appointments_patients_clinic_id_patient_id",
                        columns: x => new { x.clinic_id, x.patient_id },
                        principalTable: "patients",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_appointments_users_clinic_id_created_by_user_id",
                        columns: x => new { x.clinic_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_appointments_users_clinic_id_veterinarian_id",
                        columns: x => new { x.clinic_id, x.veterinarian_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_clinic_id_client_id_start_at",
                table: "appointments",
                columns: new[] { "clinic_id", "client_id", "start_at" });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_clinic_id_created_by_user_id",
                table: "appointments",
                columns: new[] { "clinic_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_clinic_id_patient_id_start_at",
                table: "appointments",
                columns: new[] { "clinic_id", "patient_id", "start_at" });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_clinic_id_start_at_id",
                table: "appointments",
                columns: new[] { "clinic_id", "start_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_clinic_id_veterinarian_id_start_at",
                table: "appointments",
                columns: new[] { "clinic_id", "veterinarian_id", "start_at" });

            // Not expressible in the EF model (ADR-009): two active, un-overridden appointments of one veterinarian
            // may never overlap. Half-open ranges, so back-to-back bookings are fine. Overridden bookings are exempt
            // by design; the service still checks them under its row locks.
            migrationBuilder.Sql("""
                ALTER TABLE appointments ADD CONSTRAINT ex_appointments_veterinarian_no_overlap
                EXCLUDE USING gist (
                    clinic_id WITH =,
                    veterinarian_id WITH =,
                    tstzrange(start_at, end_at, '[)') WITH &&)
                WHERE (status IN ('Scheduled', 'CheckedIn') AND override_reason IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appointments");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:CollationDefinition:case_insensitive", "und-u-ks-level2,und-u-ks-level2,icu,False")
                .OldAnnotation("Npgsql:CollationDefinition:case_insensitive", "und-u-ks-level2,und-u-ks-level2,icu,False")
                .OldAnnotation("Npgsql:PostgresExtension:btree_gist", ",,");
        }
    }
}
