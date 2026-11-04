using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Settings.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropSettingsEventSubPeriodOffsets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinishingWeekendStartOffset",
                table: "settings_event");

            migrationBuilder.DropColumn(
                name: "FirstCrewStartOffset",
                table: "settings_event");

            migrationBuilder.DropColumn(
                name: "PreEventWeekStartOffset",
                table: "settings_event");

            migrationBuilder.DropColumn(
                name: "SetupWeekStartOffset",
                table: "settings_event");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FinishingWeekendStartOffset",
                table: "settings_event",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FirstCrewStartOffset",
                table: "settings_event",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PreEventWeekStartOffset",
                table: "settings_event",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SetupWeekStartOffset",
                table: "settings_event",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
