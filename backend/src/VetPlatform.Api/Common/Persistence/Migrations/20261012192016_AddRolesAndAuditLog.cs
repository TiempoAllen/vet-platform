using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRolesAndAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    request_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_logs_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audit_logs_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => new { x.user_id, x.role });
                    table.CheckConstraint("ck_user_roles_role", "role IN ('Admin', 'Veterinarian', 'Nurse', 'Receptionist', 'Inventory')");
                    table.ForeignKey(
                        name: "fk_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_actor_user_id",
                table: "audit_logs",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_clinic_id_occurred_at",
                table: "audit_logs",
                columns: new[] { "clinic_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_entity_type_entity_id",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id" });

            // The audit log is append-only (docs/SECURITY.md section 11, ADR-004). Corrections are new entries.
            // - UPDATE, DELETE and TRUNCATE are rejected.
            // - recorded_at is always the database's clock, whatever the INSERT supplies, so the stored timeline
            //   cannot be back- or forward-dated by the application.
            // These triggers stop the application role; they do not stop the table owner from disabling them, which
            // is why production runs the API as a role that does not own the schema (ADR-004, X-05).
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_logs_reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_logs is append-only' USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE FUNCTION audit_logs_stamp_recorded_at() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.recorded_at := now();
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER audit_logs_no_update_or_delete
                    BEFORE UPDATE OR DELETE ON audit_logs
                    FOR EACH ROW EXECUTE FUNCTION audit_logs_reject_change();

                CREATE TRIGGER audit_logs_no_truncate
                    BEFORE TRUNCATE ON audit_logs
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_logs_reject_change();

                CREATE TRIGGER audit_logs_recorded_at
                    BEFORE INSERT ON audit_logs
                    FOR EACH ROW EXECUTE FUNCTION audit_logs_stamp_recorded_at();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back must never destroy audit history: refuse while the log holds any entry. Archive the table
            // (or restore a backup elsewhere) deliberately before rolling back past this migration.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM audit_logs) THEN
                        RAISE EXCEPTION 'Refusing to drop audit_logs: it contains audit history. Archive it first.';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.Sql("""
                DROP FUNCTION audit_logs_reject_change();
                DROP FUNCTION audit_logs_stamp_recorded_at();
                """);

            migrationBuilder.DropTable(
                name: "user_roles");
        }
    }
}
