using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPatients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clinic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    species = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    breed = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    sex = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reproductive_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: true),
                    date_of_birth_estimated = table.Column<bool>(type: "boolean", nullable: false),
                    markings = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    microchip_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    weight_kg = table.Column<decimal>(type: "numeric(7,3)", precision: 7, scale: 3, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    date_of_death = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patients", x => x.id);
                    table.UniqueConstraint("ak_patients_clinic_id_id", x => new { x.clinic_id, x.id });
                    table.CheckConstraint("ck_patients_date_of_birth_estimated", "NOT date_of_birth_estimated OR date_of_birth IS NOT NULL");
                    table.CheckConstraint("ck_patients_date_of_death", "date_of_death IS NULL OR (status = 'Deceased' AND (date_of_birth IS NULL OR date_of_death >= date_of_birth))");
                    table.CheckConstraint("ck_patients_microchip_number", "microchip_number ~ '^[A-Z0-9]+$'");
                    table.CheckConstraint("ck_patients_name", "btrim(name) <> ''");
                    table.CheckConstraint("ck_patients_reproductive_status", "reproductive_status IN ('Intact', 'Neutered', 'Unknown')");
                    table.CheckConstraint("ck_patients_sex", "sex IN ('Male', 'Female', 'Unknown')");
                    table.CheckConstraint("ck_patients_species", "species IN ('Dog', 'Cat', 'Bird', 'Rabbit', 'Rodent', 'Reptile', 'Horse', 'Other')");
                    table.CheckConstraint("ck_patients_status", "status IN ('Active', 'Inactive', 'Deceased')");
                    table.CheckConstraint("ck_patients_weight_kg", "weight_kg > 0 AND weight_kg <= 2000");
                    table.ForeignKey(
                        name: "fk_patients_clients_clinic_id_client_id",
                        columns: x => new { x.clinic_id, x.client_id },
                        principalTable: "clients",
                        principalColumns: new[] { "clinic_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_patients_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_patients_clinic_id_client_id_name_id",
                table: "patients",
                columns: new[] { "clinic_id", "client_id", "name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_patients_clinic_id_created_at_id",
                table: "patients",
                columns: new[] { "clinic_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_patients_clinic_id_microchip_number",
                table: "patients",
                columns: new[] { "clinic_id", "microchip_number" },
                unique: true,
                filter: "microchip_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_patients_clinic_id_name_id",
                table: "patients",
                columns: new[] { "clinic_id", "name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_patients_clinic_id_updated_at_id",
                table: "patients",
                columns: new[] { "clinic_id", "updated_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "patients");
        }
    }
}
