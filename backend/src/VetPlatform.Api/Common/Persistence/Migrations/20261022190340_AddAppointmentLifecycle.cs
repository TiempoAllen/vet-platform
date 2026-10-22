using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancellation_reason",
                table: "appointments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at",
                table: "appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancelled_by",
                table: "appointments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "checked_in_at",
                table: "appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                table: "appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "no_show_at",
                table: "appointments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "status_changed_by_user_id",
                table: "appointments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_appointments_clinic_id_status_changed_by_user_id",
                table: "appointments",
                columns: new[] { "clinic_id", "status_changed_by_user_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_appointments_cancellation",
                table: "appointments",
                sql: "(status = 'Cancelled') = (cancelled_by IS NOT NULL) AND (cancellation_reason IS NULL OR status = 'Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_appointments_cancelled_by",
                table: "appointments",
                sql: "cancelled_by IN ('Client', 'Clinic')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_appointments_lifecycle",
                table: "appointments",
                sql: "CASE status WHEN 'Scheduled' THEN checked_in_at IS NULL AND completed_at IS NULL AND no_show_at IS NULL AND cancelled_at IS NULL WHEN 'CheckedIn' THEN checked_in_at IS NOT NULL AND completed_at IS NULL AND no_show_at IS NULL AND cancelled_at IS NULL WHEN 'Completed' THEN checked_in_at IS NOT NULL AND completed_at IS NOT NULL AND no_show_at IS NULL AND cancelled_at IS NULL WHEN 'NoShow' THEN checked_in_at IS NULL AND completed_at IS NULL AND no_show_at IS NOT NULL AND cancelled_at IS NULL WHEN 'Cancelled' THEN completed_at IS NULL AND no_show_at IS NULL AND cancelled_at IS NOT NULL ELSE false END");

            migrationBuilder.AddForeignKey(
                name: "fk_appointments_users_clinic_id_status_changed_by_user_id",
                table: "appointments",
                columns: new[] { "clinic_id", "status_changed_by_user_id" },
                principalTable: "users",
                principalColumns: new[] { "clinic_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_appointments_users_clinic_id_status_changed_by_user_id",
                table: "appointments");

            migrationBuilder.DropIndex(
                name: "ix_appointments_clinic_id_status_changed_by_user_id",
                table: "appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_appointments_cancellation",
                table: "appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_appointments_cancelled_by",
                table: "appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_appointments_lifecycle",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "cancellation_reason",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "cancelled_by",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "checked_in_at",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "no_show_at",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "status_changed_by_user_id",
                table: "appointments");
        }
    }
}
