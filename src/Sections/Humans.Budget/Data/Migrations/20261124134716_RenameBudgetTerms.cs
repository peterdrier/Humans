using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Budget.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameBudgetTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "VendorEventId",
                table: "budget_years",
                newName: "PlatformEventId");

            migrationBuilder.RenameColumn(
                name: "IsDeleted",
                table: "budget_years",
                newName: "IsArchived");

            migrationBuilder.RenameColumn(
                name: "DeletedAt",
                table: "budget_years",
                newName: "ArchivedAt");

            migrationBuilder.RenameColumn(
                name: "TicketTailorFeePercent",
                table: "budget_ticketing_projections",
                newName: "PlatformFeePercent");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PlatformEventId",
                table: "budget_years",
                newName: "VendorEventId");

            migrationBuilder.RenameColumn(
                name: "IsArchived",
                table: "budget_years",
                newName: "IsDeleted");

            migrationBuilder.RenameColumn(
                name: "ArchivedAt",
                table: "budget_years",
                newName: "DeletedAt");

            migrationBuilder.RenameColumn(
                name: "PlatformFeePercent",
                table: "budget_ticketing_projections",
                newName: "TicketTailorFeePercent");
        }
    }
}
