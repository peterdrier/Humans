using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Budget.Data.Migrations
{
    /// <inheritdoc />
    public partial class BudgetYearVendorEventId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VendorEventId",
                table: "budget_years",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VendorEventId",
                table: "budget_years");
        }
    }
}
