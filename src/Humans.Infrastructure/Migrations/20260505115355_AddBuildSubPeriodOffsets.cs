using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildSubPeriodOffsets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FinishingWeekendStartOffset",
                table: "event_settings",
                type: "integer",
                nullable: false,
                defaultValue: -4);

            migrationBuilder.AddColumn<int>(
                name: "FirstCrewStartOffset",
                table: "event_settings",
                type: "integer",
                nullable: false,
                defaultValue: -25);

            migrationBuilder.AddColumn<int>(
                name: "PreEventWeekStartOffset",
                table: "event_settings",
                type: "integer",
                nullable: false,
                defaultValue: -9);

            migrationBuilder.AddColumn<int>(
                name: "SetupWeekStartOffset",
                table: "event_settings",
                type: "integer",
                nullable: false,
                defaultValue: -16);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinishingWeekendStartOffset",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "FirstCrewStartOffset",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "PreEventWeekStartOffset",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "SetupWeekStartOffset",
                table: "event_settings");
        }
    }
}
