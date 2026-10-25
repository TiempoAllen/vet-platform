using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVaccinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vaccinations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consultation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vaccine_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    lot_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    lot_expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    administered_on = table.Column<DateOnly>(type: "date", nullable: false),
                    administered_day_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    administered_elsewhere = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    veterinarian_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vaccinations", x => x.id);
                    table.CheckConstraint("ck_vaccinations_dates", "administered_on BETWEEN DATE '2000-01-01' AND DATE '2100-12-31' AND (next_due_on IS NULL OR (next_due_on > administered_on AND next_due_on <= DATE '2100-12-31')) AND (lot_expires_on IS NULL OR lot_expires_on BETWEEN DATE '2000-01-01' AND DATE '2100-12-31')");
                    table.CheckConstraint("ck_vaccinations_given_by", "(administered_elsewhere IS NULL AND veterinarian_id IS NOT NULL) OR (administered_elsewhere IS NOT NULL AND btrim(administered_elsewhere) <> '' AND veterinarian_id IS NULL)");
                    table.CheckConstraint("ck_vaccinations_lifecycle", "CASE status WHEN 'Active' THEN voided_at IS NULL AND voided_by_user_id IS NULL AND void_reason IS NULL WHEN 'Voided' THEN voided_at IS NOT NULL AND voided_by_user_id IS NOT NULL AND void_reason IS NOT NULL AND btrim(void_reason) <> '' ELSE false END");
                    table.CheckConstraint("ck_vaccinations_status", "status IN ('Active', 'Voided')");
                    table.CheckConstraint("ck_vaccinations_vaccine_name", "btrim(vaccine_name) <> ''");
                    table.ForeignKey(
                        name: "fk_vaccinations_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vaccinations_consultations_clinic_id_patient_id_consultatio",
                        columns: x => new { x.clinic_id, x.patient_id, x.consultation_id },
                        principalTable: "consultations",
                        principalColumns: new[] { "clinic_id", "patient_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vaccinations_patients_clinic_id_patient_id",
                        columns: x => new { x.clinic_id, x.patient_id },
                        principalTable: "patients",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vaccinations_users_clinic_id_recorded_by_user_id",
                        columns: x => new { x.clinic_id, x.recorded_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vaccinations_users_clinic_id_veterinarian_id",
                        columns: x => new { x.clinic_id, x.veterinarian_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vaccinations_users_clinic_id_voided_by_user_id",
                        columns: x => new { x.clinic_id, x.voided_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vaccinations_clinic_id_patient_id_administered_on",
                table: "vaccinations",
                columns: new[] { "clinic_id", "patient_id", "administered_on" });

            migrationBuilder.CreateIndex(
                name: "ix_vaccinations_clinic_id_patient_id_consultation_id",
                table: "vaccinations",
                columns: new[] { "clinic_id", "patient_id", "consultation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vaccinations_clinic_id_recorded_by_user_id",
                table: "vaccinations",
                columns: new[] { "clinic_id", "recorded_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vaccinations_clinic_id_veterinarian_id",
                table: "vaccinations",
                columns: new[] { "clinic_id", "veterinarian_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vaccinations_clinic_id_voided_by_user_id",
                table: "vaccinations",
                columns: new[] { "clinic_id", "voided_by_user_id" });

            // Vaccination records never change once saved (ADR-014): no DELETE, no TRUNCATE, and the only UPDATE is the
            // one-way step Active -> Voided, which may touch nothing but the status, the void fields and updated_at
            // (compared as whole rows, so future columns are covered). A new row is Active and may not link a discarded
            // consultation; the link's parent is read FOR SHARE so a concurrent discard is waited for.
            migrationBuilder.Sql("""
                CREATE FUNCTION vaccinations_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Active' THEN
                            RAISE EXCEPTION 'a vaccination is recorded as Active' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        IF NEW.consultation_id IS NOT NULL AND EXISTS (
                            SELECT 1 FROM consultations
                            WHERE id = NEW.consultation_id AND clinic_id = NEW.clinic_id AND status = 'Discarded'
                            FOR SHARE) THEN
                            RAISE EXCEPTION 'a vaccination cannot be linked to a discarded consultation'
                                USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'UPDATE' AND OLD.status = 'Active' AND NEW.status = 'Voided'
                        AND (to_jsonb(NEW) - 'status' - 'voided_at' - 'voided_by_user_id' - 'void_reason' - 'updated_at')
                          = (to_jsonb(OLD) - 'status' - 'voided_at' - 'voided_by_user_id' - 'void_reason' - 'updated_at') THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'vaccination records cannot be changed or deleted; void them instead'
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER vaccinations_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON vaccinations
                    FOR EACH ROW EXECUTE FUNCTION vaccinations_guard();

                CREATE TRIGGER vaccinations_no_truncate
                    BEFORE TRUNCATE ON vaccinations
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently destroy clinical records.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM vaccinations) THEN
                        RAISE EXCEPTION 'Refusing to drop vaccinations: they contain clinical records. Archive them first.';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.DropTable(
                name: "vaccinations");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS vaccinations_guard();");
        }
    }
}
