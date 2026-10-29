using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Shifts.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildSubPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SubPeriodFinishingWeekendStart",
                table: "event_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubPeriodFirstCrewStart",
                table: "event_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubPeriodPreEventWeekStart",
                table: "event_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubPeriodSetupWeekStart",
                table: "event_settings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubPeriodFinishingWeekendStart",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "SubPeriodFirstCrewStart",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "SubPeriodPreEventWeekStart",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "SubPeriodSetupWeekStart",
                table: "event_settings");
        }
    }
}
