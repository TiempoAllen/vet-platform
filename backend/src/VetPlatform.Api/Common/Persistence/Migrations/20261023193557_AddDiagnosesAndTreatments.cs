using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiagnosesAndTreatments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_consultations_clinic_id_patient_id_id",
                table: "consultations",
                columns: new[] { "clinic_id", "patient_id", "id" });

            migrationBuilder.CreateTable(
                name: "diagnoses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consultation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    code_system = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_diagnoses", x => x.id);
                    table.UniqueConstraint("ak_diagnoses_clinic_id_consultation_id_id", x => new { x.clinic_id, x.consultation_id, x.id });
                    table.CheckConstraint("ck_diagnoses_code_system", "code_system IS NULL OR code IS NOT NULL");
                    table.CheckConstraint("ck_diagnoses_name", "btrim(name) <> ''");
                    table.CheckConstraint("ck_diagnoses_status", "status IN ('Provisional', 'Confirmed', 'RuledOut')");
                    table.ForeignKey(
                        name: "fk_diagnoses_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_diagnoses_consultations_clinic_id_patient_id_consultation_id",
                        columns: x => new { x.clinic_id, x.patient_id, x.consultation_id },
                        principalTable: "consultations",
                        principalColumns: new[] { "clinic_id", "patient_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_diagnoses_users_clinic_id_created_by_user_id",
                        columns: x => new { x.clinic_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "treatments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consultation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    diagnosis_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    dose = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    route = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    frequency = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    duration = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_treatments", x => x.id);
                    table.CheckConstraint("ck_treatments_name", "btrim(name) <> ''");
                    table.CheckConstraint("ck_treatments_type", "type IN ('Procedure', 'Medication', 'Fluids', 'Other')");
                    table.ForeignKey(
                        name: "fk_treatments_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_treatments_consultations_clinic_id_patient_id_consultation_",
                        columns: x => new { x.clinic_id, x.patient_id, x.consultation_id },
                        principalTable: "consultations",
                        principalColumns: new[] { "clinic_id", "patient_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_treatments_diagnoses_clinic_id_consultation_id_diagnosis_id",
                        columns: x => new { x.clinic_id, x.consultation_id, x.diagnosis_id },
                        principalTable: "diagnoses",
                        principalColumns: new[] { "clinic_id", "consultation_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_treatments_users_clinic_id_created_by_user_id",
                        columns: x => new { x.clinic_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_diagnoses_clinic_id_created_by_user_id",
                table: "diagnoses",
                columns: new[] { "clinic_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_diagnoses_clinic_id_patient_id_consultation_id",
                table: "diagnoses",
                columns: new[] { "clinic_id", "patient_id", "consultation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_diagnoses_clinic_id_patient_id_created_at",
                table: "diagnoses",
                columns: new[] { "clinic_id", "patient_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_diagnoses_one_primary_per_consultation",
                table: "diagnoses",
                column: "consultation_id",
                unique: true,
                filter: "is_primary");

            migrationBuilder.CreateIndex(
                name: "ix_treatments_clinic_id_consultation_id",
                table: "treatments",
                columns: new[] { "clinic_id", "consultation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_treatments_clinic_id_consultation_id_diagnosis_id",
                table: "treatments",
                columns: new[] { "clinic_id", "consultation_id", "diagnosis_id" });

            migrationBuilder.CreateIndex(
                name: "ix_treatments_clinic_id_created_by_user_id",
                table: "treatments",
                columns: new[] { "clinic_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_treatments_clinic_id_patient_id_consultation_id",
                table: "treatments",
                columns: new[] { "clinic_id", "patient_id", "consultation_id" });
        
            // Diagnoses and treatments follow their consultation (ADR-012), enforced by the database as well:
            // - while the consultation is InProgress, anything goes;
            // - once it is Completed, only a diagnosis's status may change (the service also requires a veterinarian);
            // - treatments, and diagnoses of a Discarded consultation, are frozen entirely.
            migrationBuilder.Sql("""
                CREATE FUNCTION diagnoses_follow_consultation() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    parent_status text;
                BEGIN
                    SELECT status INTO parent_status FROM consultations
                    WHERE id = COALESCE(NEW.consultation_id, OLD.consultation_id)
                      AND clinic_id = COALESCE(NEW.clinic_id, OLD.clinic_id);
                    IF parent_status = 'InProgress' THEN
                        RETURN COALESCE(NEW, OLD);
                    END IF;
                    IF TG_OP = 'UPDATE' AND parent_status = 'Completed'
                        AND NEW.clinic_id = OLD.clinic_id AND NEW.consultation_id = OLD.consultation_id
                        AND NEW.patient_id = OLD.patient_id AND NEW.name = OLD.name
                        AND NEW.code IS NOT DISTINCT FROM OLD.code AND NEW.code_system IS NOT DISTINCT FROM OLD.code_system
                        AND NEW.is_primary = OLD.is_primary AND NEW.notes IS NOT DISTINCT FROM OLD.notes
                        AND NEW.created_by_user_id IS NOT DISTINCT FROM OLD.created_by_user_id
                        AND NEW.created_at = OLD.created_at THEN
                        RETURN NEW; -- status (and updated_at) only
                    END IF;
                    RAISE EXCEPTION 'diagnoses of a % consultation cannot be changed this way', parent_status
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER diagnoses_follow_consultation
                    BEFORE INSERT OR UPDATE OR DELETE ON diagnoses
                    FOR EACH ROW EXECUTE FUNCTION diagnoses_follow_consultation();

                CREATE FUNCTION treatments_follow_consultation() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    parent_status text;
                BEGIN
                    SELECT status INTO parent_status FROM consultations
                    WHERE id = COALESCE(NEW.consultation_id, OLD.consultation_id)
                      AND clinic_id = COALESCE(NEW.clinic_id, OLD.clinic_id);
                    IF parent_status = 'InProgress' THEN
                        RETURN COALESCE(NEW, OLD);
                    END IF;
                    RAISE EXCEPTION 'treatments of a % consultation cannot be changed', parent_status
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER treatments_follow_consultation
                    BEFORE INSERT OR UPDATE OR DELETE ON treatments
                    FOR EACH ROW EXECUTE FUNCTION treatments_follow_consultation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently destroy clinical records.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM diagnoses) OR EXISTS (SELECT 1 FROM treatments) THEN
                        RAISE EXCEPTION 'Refusing to drop diagnoses and treatments: they contain clinical records. Archive them first.';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.DropTable(
                name: "treatments");

            migrationBuilder.DropTable(
                name: "diagnoses");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_consultations_clinic_id_patient_id_id",
                table: "consultations");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS diagnoses_follow_consultation();
                DROP FUNCTION IF EXISTS treatments_follow_consultation();
                """);
        }
    }
}
