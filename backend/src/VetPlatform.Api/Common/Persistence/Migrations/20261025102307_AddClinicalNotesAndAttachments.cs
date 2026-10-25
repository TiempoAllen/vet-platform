using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClinicalNotesAndAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consultation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    storage_key = table.Column<string>(type: "character(32)", fixedLength: true, maxLength: 32, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawn_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    withdraw_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachments", x => x.id);
                    table.CheckConstraint("ck_attachments_content_type", "content_type IN ('image/jpeg', 'image/png', 'image/webp', 'application/pdf')");
                    table.CheckConstraint("ck_attachments_file_name", "btrim(file_name) <> ''");
                    table.CheckConstraint("ck_attachments_lifecycle", "CASE status WHEN 'Active' THEN withdrawn_at IS NULL AND withdrawn_by_user_id IS NULL AND withdraw_reason IS NULL WHEN 'Withdrawn' THEN withdrawn_at IS NOT NULL AND withdrawn_by_user_id IS NOT NULL AND withdraw_reason IS NOT NULL AND btrim(withdraw_reason) <> '' ELSE false END");
                    table.CheckConstraint("ck_attachments_owner", "(client_id IS NOT NULL AND patient_id IS NULL AND consultation_id IS NULL) OR (client_id IS NULL AND patient_id IS NOT NULL)");
                    table.CheckConstraint("ck_attachments_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_attachments_size_bytes", "size_bytes BETWEEN 1 AND 20971520");
                    table.CheckConstraint("ck_attachments_status", "status IN ('Active', 'Withdrawn')");
                    table.CheckConstraint("ck_attachments_storage_key", "storage_key ~ '^[0-9a-f]{32}$'");
                    table.ForeignKey(
                        name: "fk_attachments_clients_clinic_id_client_id",
                        columns: x => new { x.clinic_id, x.client_id },
                        principalTable: "clients",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attachments_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attachments_consultations_clinic_id_patient_id_consultation",
                        columns: x => new { x.clinic_id, x.patient_id, x.consultation_id },
                        principalTable: "consultations",
                        principalColumns: new[] { "clinic_id", "patient_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attachments_patients_clinic_id_patient_id",
                        columns: x => new { x.clinic_id, x.patient_id },
                        principalTable: "patients",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attachments_users_clinic_id_uploaded_by_user_id",
                        columns: x => new { x.clinic_id, x.uploaded_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attachments_users_clinic_id_withdrawn_by_user_id",
                        columns: x => new { x.clinic_id, x.withdrawn_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "clinical_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consultation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clinical_notes", x => x.id);
                    table.CheckConstraint("ck_clinical_notes_text", "btrim(text) <> ''");
                    table.ForeignKey(
                        name: "fk_clinical_notes_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_clinical_notes_consultations_clinic_id_patient_id_consultat",
                        columns: x => new { x.clinic_id, x.patient_id, x.consultation_id },
                        principalTable: "consultations",
                        principalColumns: new[] { "clinic_id", "patient_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_clinical_notes_patients_clinic_id_patient_id",
                        columns: x => new { x.clinic_id, x.patient_id },
                        principalTable: "patients",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_clinical_notes_users_clinic_id_author_user_id",
                        columns: x => new { x.clinic_id, x.author_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_clinic_id_client_id_created_at",
                table: "attachments",
                columns: new[] { "clinic_id", "client_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_clinic_id_patient_id_consultation_id",
                table: "attachments",
                columns: new[] { "clinic_id", "patient_id", "consultation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_clinic_id_patient_id_created_at",
                table: "attachments",
                columns: new[] { "clinic_id", "patient_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_clinic_id_uploaded_by_user_id",
                table: "attachments",
                columns: new[] { "clinic_id", "uploaded_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_clinic_id_withdrawn_by_user_id",
                table: "attachments",
                columns: new[] { "clinic_id", "withdrawn_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_storage_key",
                table: "attachments",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clinical_notes_clinic_id_author_user_id",
                table: "clinical_notes",
                columns: new[] { "clinic_id", "author_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_clinical_notes_clinic_id_patient_id_consultation_id",
                table: "clinical_notes",
                columns: new[] { "clinic_id", "patient_id", "consultation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_clinical_notes_clinic_id_patient_id_created_at",
                table: "clinical_notes",
                columns: new[] { "clinic_id", "patient_id", "created_at" });

            // Clinical notes are append-only and attachment rows never change except Active -> Withdrawn (ADR-015). A
            // linked consultation's status is read FOR SHARE first and only then tested, so an insert waits for a
            // concurrent discard instead of reading the old status (the B-14 lesson).
            migrationBuilder.Sql("""
                CREATE FUNCTION clinical_records_refuse_discarded_link(p_clinic_id uuid, p_consultation_id uuid, p_table text)
                RETURNS void LANGUAGE plpgsql AS $$
                DECLARE
                    parent_status text;
                BEGIN
                    IF p_consultation_id IS NULL THEN
                        RETURN;
                    END IF;
                    SELECT status INTO parent_status FROM consultations
                    WHERE id = p_consultation_id AND clinic_id = p_clinic_id
                    FOR SHARE;
                    IF parent_status = 'Discarded' THEN
                        RAISE EXCEPTION '% cannot be linked to a discarded consultation', p_table
                            USING ERRCODE = 'insufficient_privilege';
                    END IF;
                END;
                $$;

                CREATE FUNCTION clinical_notes_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        PERFORM clinical_records_refuse_discarded_link(NEW.clinic_id, NEW.consultation_id, TG_TABLE_NAME);
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'clinical notes are append-only' USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER clinical_notes_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON clinical_notes
                    FOR EACH ROW EXECUTE FUNCTION clinical_notes_guard();

                CREATE TRIGGER clinical_notes_no_truncate
                    BEFORE TRUNCATE ON clinical_notes
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();

                CREATE FUNCTION attachments_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Active' THEN
                            RAISE EXCEPTION 'an attachment is stored as Active' USING ERRCODE = 'insufficient_privilege';
                        END IF;
                        PERFORM clinical_records_refuse_discarded_link(NEW.clinic_id, NEW.consultation_id, TG_TABLE_NAME);
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'UPDATE' AND OLD.status = 'Active' AND NEW.status = 'Withdrawn'
                        AND (to_jsonb(NEW) - 'status' - 'withdrawn_at' - 'withdrawn_by_user_id' - 'withdraw_reason' - 'updated_at')
                          = (to_jsonb(OLD) - 'status' - 'withdrawn_at' - 'withdrawn_by_user_id' - 'withdraw_reason' - 'updated_at') THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'attachments cannot be changed or deleted; withdraw them instead'
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER attachments_guard
                    BEFORE INSERT OR UPDATE OR DELETE ON attachments
                    FOR EACH ROW EXECUTE FUNCTION attachments_guard();

                CREATE TRIGGER attachments_no_truncate
                    BEFORE TRUNCATE ON attachments
                    FOR EACH STATEMENT EXECUTE FUNCTION clinical_items_reject_truncate();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently destroy clinical records or the references to stored files.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM clinical_notes) OR EXISTS (SELECT 1 FROM attachments) THEN
                        RAISE EXCEPTION 'Refusing to drop clinical_notes and attachments: they contain records. Archive them first.';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.DropTable(
                name: "clinical_notes");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS attachments_guard();
                DROP FUNCTION IF EXISTS clinical_notes_guard();
                DROP FUNCTION IF EXISTS clinical_records_refuse_discarded_link(uuid, uuid, text);
                """);
        }
    }
}
