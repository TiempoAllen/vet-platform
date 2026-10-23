using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConsultations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consultations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    veterinarian_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    discarded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    discard_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    subjective = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    objective = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    assessment = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    plan = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    weight_kg = table.Column<decimal>(type: "numeric(7,3)", precision: 7, scale: 3, nullable: true),
                    temperature_c = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    heart_rate = table.Column<int>(type: "integer", nullable: true),
                    respiratory_rate = table.Column<int>(type: "integer", nullable: true),
                    body_condition_score = table.Column<int>(type: "integer", nullable: true),
                    pain_score = table.Column<int>(type: "integer", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consultations", x => x.id);
                    table.UniqueConstraint("ak_consultations_clinic_id_id", x => new { x.clinic_id, x.id });
                    table.CheckConstraint("ck_consultations_body_condition_score", "body_condition_score BETWEEN 1 AND 9");
                    table.CheckConstraint("ck_consultations_heart_rate", "heart_rate BETWEEN 1 AND 400");
                    table.CheckConstraint("ck_consultations_lifecycle", "CASE status WHEN 'InProgress' THEN completed_at IS NULL AND completed_by_user_id IS NULL AND discarded_at IS NULL AND discard_reason IS NULL WHEN 'Completed' THEN completed_at IS NOT NULL AND completed_by_user_id IS NOT NULL AND completed_at >= started_at AND discarded_at IS NULL AND discard_reason IS NULL WHEN 'Discarded' THEN completed_at IS NULL AND discarded_at IS NOT NULL AND discard_reason IS NOT NULL ELSE false END");
                    table.CheckConstraint("ck_consultations_pain_score", "pain_score BETWEEN 0 AND 10");
                    table.CheckConstraint("ck_consultations_respiratory_rate", "respiratory_rate BETWEEN 1 AND 200");
                    table.CheckConstraint("ck_consultations_status", "status IN ('InProgress', 'Completed', 'Discarded')");
                    table.CheckConstraint("ck_consultations_temperature_c", "temperature_c BETWEEN 25 AND 45");
                    table.CheckConstraint("ck_consultations_weight_kg", "weight_kg > 0 AND weight_kg <= 2000");
                    table.ForeignKey(
                        name: "fk_consultations_appointments_clinic_id_appointment_id",
                        columns: x => new { x.clinic_id, x.appointment_id },
                        principalTable: "appointments",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consultations_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consultations_patients_clinic_id_patient_id",
                        columns: x => new { x.clinic_id, x.patient_id },
                        principalTable: "patients",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consultations_users_clinic_id_completed_by_user_id",
                        columns: x => new { x.clinic_id, x.completed_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consultations_users_clinic_id_created_by_user_id",
                        columns: x => new { x.clinic_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consultations_users_clinic_id_veterinarian_id",
                        columns: x => new { x.clinic_id, x.veterinarian_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consultation_addenda",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consultation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consultation_addenda", x => x.id);
                    table.CheckConstraint("ck_consultation_addenda_text", "btrim(text) <> ''");
                    table.ForeignKey(
                        name: "fk_consultation_addenda_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consultation_addenda_consultations_clinic_id_consultation_id",
                        columns: x => new { x.clinic_id, x.consultation_id },
                        principalTable: "consultations",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consultation_addenda_users_clinic_id_author_user_id",
                        columns: x => new { x.clinic_id, x.author_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_consultation_addenda_clinic_id_author_user_id",
                table: "consultation_addenda",
                columns: new[] { "clinic_id", "author_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consultation_addenda_clinic_id_consultation_id_created_at",
                table: "consultation_addenda",
                columns: new[] { "clinic_id", "consultation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_consultations_clinic_id_completed_by_user_id",
                table: "consultations",
                columns: new[] { "clinic_id", "completed_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consultations_clinic_id_created_by_user_id",
                table: "consultations",
                columns: new[] { "clinic_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consultations_clinic_id_patient_id_started_at",
                table: "consultations",
                columns: new[] { "clinic_id", "patient_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_consultations_clinic_id_veterinarian_id",
                table: "consultations",
                columns: new[] { "clinic_id", "veterinarian_id" });

            migrationBuilder.CreateIndex(
                name: "ux_consultations_one_open_per_patient",
                table: "consultations",
                columns: new[] { "clinic_id", "patient_id" },
                unique: true,
                filter: "status = 'InProgress'");

            migrationBuilder.CreateIndex(
                name: "ux_consultations_one_per_appointment",
                table: "consultations",
                columns: new[] { "clinic_id", "appointment_id" },
                unique: true,
                filter: "appointment_id IS NOT NULL AND status <> 'Discarded'");
        
            // Addenda are the append-only correction trail of signed consultations (FR-CLN-2, ADR-011): the database
            // itself refuses UPDATE, DELETE and TRUNCATE, like audit_logs. As there, the table owner can still disable
            // the triggers, which is why production runs the API as a non-owner role (X-05).
            migrationBuilder.Sql("""
                CREATE FUNCTION consultation_addenda_reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'consultation_addenda is append-only' USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER consultation_addenda_no_update_or_delete
                    BEFORE UPDATE OR DELETE ON consultation_addenda
                    FOR EACH ROW EXECUTE FUNCTION consultation_addenda_reject_change();

                CREATE TRIGGER consultation_addenda_no_truncate
                    BEFORE TRUNCATE ON consultation_addenda
                    FOR EACH STATEMENT EXECUTE FUNCTION consultation_addenda_reject_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently destroy signed clinical corrections: refuse while any exist.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM consultation_addenda) THEN
                        RAISE EXCEPTION 'Refusing to drop consultation_addenda: it contains clinical corrections.';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.DropTable(
                name: "consultation_addenda");

            migrationBuilder.DropTable(
                name: "consultations");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS consultation_addenda_reject_change();");
        }
    }
}
