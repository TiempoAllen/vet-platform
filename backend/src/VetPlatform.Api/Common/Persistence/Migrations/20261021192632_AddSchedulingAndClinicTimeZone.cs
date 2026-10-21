using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedulingAndClinicTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "time_zone",
                table: "clinics",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "UTC");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_users_clinic_id_id",
                table: "users",
                columns: new[] { "clinic_id", "id" });

            migrationBuilder.CreateTable(
                name: "schedule_exceptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    veterinarian_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedule_exceptions", x => x.id);
                    table.CheckConstraint("ck_schedule_exceptions_closure", "(type = 'Closure') = (veterinarian_id IS NULL)");
                    table.CheckConstraint("ck_schedule_exceptions_granularity", "extract(epoch FROM starts_at) % 300 = 0 AND extract(epoch FROM ends_at) % 300 = 0");
                    table.CheckConstraint("ck_schedule_exceptions_range", "ends_at > starts_at");
                    table.CheckConstraint("ck_schedule_exceptions_type", "type IN ('Leave', 'Sickness', 'Training', 'Other', 'Closure')");
                    table.ForeignKey(
                        name: "fk_schedule_exceptions_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_schedule_exceptions_users_clinic_id_veterinarian_id",
                        columns: x => new { x.clinic_id, x.veterinarian_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "working_hours",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    veterinarian_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    start_minute = table.Column<int>(type: "integer", nullable: false),
                    end_minute = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_working_hours", x => x.id);
                    table.CheckConstraint("ck_working_hours_day_of_week", "day_of_week IN ('Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday')");
                    table.CheckConstraint("ck_working_hours_effective", "effective_to IS NULL OR effective_to >= effective_from");
                    table.CheckConstraint("ck_working_hours_minutes", "start_minute >= 0 AND end_minute <= 1440 AND start_minute < end_minute AND start_minute % 5 = 0 AND end_minute % 5 = 0");
                    table.ForeignKey(
                        name: "fk_working_hours_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_working_hours_users_clinic_id_veterinarian_id",
                        columns: x => new { x.clinic_id, x.veterinarian_id },
                        principalTable: "users",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_schedule_exceptions_clinic_id_starts_at_id",
                table: "schedule_exceptions",
                columns: new[] { "clinic_id", "starts_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_schedule_exceptions_clinic_id_veterinarian_id_starts_at",
                table: "schedule_exceptions",
                columns: new[] { "clinic_id", "veterinarian_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_working_hours_clinic_id_veterinarian_id_day_of_week_start_m",
                table: "working_hours",
                columns: new[] { "clinic_id", "veterinarian_id", "day_of_week", "start_minute" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedule_exceptions");

            migrationBuilder.DropTable(
                name: "working_hours");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_users_clinic_id_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "time_zone",
                table: "clinics");
        }
    }
}
