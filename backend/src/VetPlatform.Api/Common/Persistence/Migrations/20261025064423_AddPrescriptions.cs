using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "prescriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consultation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    diagnosis_id = table.Column<Guid>(type: "uuid", nullable: true),
                    medication_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    dose = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    route = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    frequency = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    duration = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    quantity_unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    refills = table.Column<int>(type: "integer", nullable: false),
                    instructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    prescribed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prescriptions", x => x.id);
                    table.CheckConstraint("ck_prescriptions_dose", "btrim(dose) <> ''");
                    table.CheckConstraint("ck_prescriptions_frequency", "btrim(frequency) <> ''");
                    table.CheckConstraint("ck_prescriptions_lifecycle", "CASE status WHEN 'Active' THEN cancelled_at IS NULL AND cancelled_by_user_id IS NULL AND cancel_reason IS NULL WHEN 'Cancelled' THEN cancelled_at IS NOT NULL AND cancelled_by_user_id IS NOT NULL AND cancel_reason IS NOT NULL AND btrim(cancel_reason) <> '' ELSE false END");
                    table.CheckConstraint("ck_prescriptions_medication_name", "btrim(medication_name) <> ''");
                    table.CheckConstraint("ck_prescriptions_quantity", "quantity > 0 AND quantity <= 100000");
                    table.CheckConstraint("ck_prescriptions_quantity_unit", "btrim(quantity_unit) <> ''");
                    table.CheckConstraint("ck_prescriptions_refills", "refills BETWEEN 0 AND 12");
                    table.CheckConstraint("ck_prescriptions_status", "status IN ('Active', 'Cancelled')");
                    table.ForeignKey(
                        name: "fk_prescriptions_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_prescriptions_consultations_clinic_id_patient_id_consultati",
                        columns: x => new { x.clinic_id, x.patient_id, x.consultation_id },
                        principalTable: "consultations",
                        principalColumns: new[] { "clinic_id", "patient_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_prescriptions_diagnoses_clinic_id_consultation_id_diagnosis",
                        columns: x => new { x.clinic_id, x.consultation_id, x.diagnosis_id },
                        principalTable: "diagnoses",
                        principalColumns: new[] { "clinic_id", "consultation_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_prescriptions_users_clinic_id_cancelled_by_user_id",
                        columns: x => new { x.clinic_id, x.cancelled_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_prescriptions_users_clinic_id_prescribed_by_user_id",
                        columns: x => new { x.clinic_id, x.prescribed_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_prescriptions_clinic_id_cancelled_by_user_id",
                table: "prescriptions",
                columns: new[] { "clinic_id", "cancelled_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_prescriptions_clinic_id_consultation_id_diagnosis_id",
                table: "prescriptions",
                columns: new[] { "clinic_id", "consultation_id", "diagnosis_id" });

            migrationBuilder.CreateIndex(
                name: "ix_prescriptions_clinic_id_patient_id_consultation_id",
                table: "prescriptions",
                columns: new[] { "clinic_id", "patient_id", "consultation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_prescriptions_clinic_id_prescribed_by_user_id",
                table: "prescriptions",
                columns: new[] { "clinic_id", "prescribed_by_user_id" });

            // Prescriptions follow their consultation (ADR-013): anything while it is in progress, except being
            // cancelled; on a completed one only the single step Active -> Cancelled, which may touch nothing but the
            // status, the cancellation fields and updated_at (compared as whole rows, so future columns are covered);
            // nothing on a discarded one. The parent is read FOR SHARE so a concurrent completion is waited for. A row
            // never moves to another consultation (or clinic or patient): the check reads only the new parent, so a
            // move would otherwise take a frozen row out of a completed consultation.
            migrationBuilder.Sql("""
                CREATE FUNCTION prescriptions_follow_consultation() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    parent_status text;
                BEGIN
                    IF TG_OP = 'UPDATE' AND (NEW.clinic_id, NEW.patient_id, NEW.consultation_id)
                        IS DISTINCT FROM (OLD.clinic_id, OLD.patient_id, OLD.consultation_id) THEN
                        RAISE EXCEPTION 'a prescription cannot move to another consultation'
                            USING ERRCODE = 'insufficient_privilege';
                    END IF;
                    SELECT status INTO parent_status FROM consultations
                    WHERE id = COALESCE(NEW.consultation_id, OLD.consultation_id)
                      AND clinic_id = COALESCE(NEW.clinic_id, OLD.clinic_id)
                    FOR SHARE;
                    IF parent_status IS NULL THEN
                        RETURN COALESCE(NEW, OLD); -- a missing parent is left to the foreign key
                    END IF;
                    IF parent_status = 'InProgress' AND (TG_OP = 'DELETE' OR NEW.status = 'Active') THEN
                        RETURN COALESCE(NEW, OLD);
                    END IF;
                    IF TG_OP = 'UPDATE' AND parent_status = 'Completed'
                        AND OLD.status = 'Active' AND NEW.status = 'Cancelled'
                        AND (to_jsonb(NEW) - 'status' - 'cancelled_at' - 'cancelled_by_user_id' - 'cancel_reason' - 'updated_at')
                          = (to_jsonb(OLD) - 'status' - 'cancelled_at' - 'cancelled_by_user_id' - 'cancel_reason' - 'updated_at') THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'prescriptions of a % consultation cannot be changed this way', parent_status
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER prescriptions_follow_consultation
                    BEFORE INSERT OR UPDATE OR DELETE ON prescriptions
                    FOR EACH ROW EXECUTE FUNCTION prescriptions_follow_consultation();

                CREATE TRIGGER prescriptions_no_truncate
                    BEFORE TRUNCATE ON prescriptions
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();
                """);

            // The B-12 triggers had the same gap (found in the B-13 review): diagnoses and treatments never move either.
            migrationBuilder.Sql(FollowConsultationFunctions(refuseMoves: true));
        }

        /// <summary>
        /// The B-12 trigger functions for diagnoses and treatments (ADR-012), with or without the check that a row stays
        /// on its consultation. Down restores the B-12 versions.
        /// </summary>
        private static string FollowConsultationFunctions(bool refuseMoves)
        {
            string Guard(string what) => refuseMoves
                ? $"""
                    IF TG_OP = 'UPDATE' AND (NEW.clinic_id, NEW.patient_id, NEW.consultation_id)
                        IS DISTINCT FROM (OLD.clinic_id, OLD.patient_id, OLD.consultation_id) THEN
                        RAISE EXCEPTION 'a {what} cannot move to another consultation'
                            USING ERRCODE = 'insufficient_privilege';
                    END IF;
                """
                : "";

            return $$"""
                CREATE OR REPLACE FUNCTION diagnoses_follow_consultation() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    parent_status text;
                BEGIN
                {{Guard("diagnosis")}}
                    -- FOR SHARE: wait for a concurrent completion of the consultation instead of reading its old status.
                    SELECT status INTO parent_status FROM consultations
                    WHERE id = COALESCE(NEW.consultation_id, OLD.consultation_id)
                      AND clinic_id = COALESCE(NEW.clinic_id, OLD.clinic_id)
                    FOR SHARE;
                    IF parent_status IS NULL OR parent_status = 'InProgress' THEN
                        RETURN COALESCE(NEW, OLD); -- a missing parent is left to the foreign key
                    END IF;
                    -- On a completed consultation only status (and updated_at) may change; every other column, present
                    -- or future, must stay as it was.
                    IF TG_OP = 'UPDATE' AND parent_status = 'Completed'
                        AND (to_jsonb(NEW) - 'status' - 'updated_at') = (to_jsonb(OLD) - 'status' - 'updated_at') THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'diagnoses of a % consultation cannot be changed this way', parent_status
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE OR REPLACE FUNCTION treatments_follow_consultation() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    parent_status text;
                BEGIN
                {{Guard("treatment")}}
                    SELECT status INTO parent_status FROM consultations
                    WHERE id = COALESCE(NEW.consultation_id, OLD.consultation_id)
                      AND clinic_id = COALESCE(NEW.clinic_id, OLD.clinic_id)
                    FOR SHARE;
                    IF parent_status IS NULL OR parent_status = 'InProgress' THEN
                        RETURN COALESCE(NEW, OLD);
                    END IF;
                    RAISE EXCEPTION 'treatments of a % consultation cannot be changed', parent_status
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                """;
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently destroy clinical records.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM prescriptions) THEN
                        RAISE EXCEPTION 'Refusing to drop prescriptions: they contain clinical records. Archive them first.';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.DropTable(
                name: "prescriptions");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS prescriptions_follow_consultation();");
            migrationBuilder.Sql(FollowConsultationFunctions(refuseMoves: false));
        }
    }
}
