using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VetPlatform.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExpiringLotsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_stock_lots_expiring",
                table: "stock_lots",
                columns: new[] { "clinic_id", "expires_on" },
                filter: "quantity > 0 AND expires_on IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stock_lots_expiring",
                table: "stock_lots");
        }
    }
}
