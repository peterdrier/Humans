using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Shifts.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropShiftsEventSettingsCalendarColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_event_settings_IsActive",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "BarriosEarlyEntryAllocation",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "BuildStartOffset",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "EarlyEntryCapacity",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "EarlyEntryClose",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "EventEndOffset",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "EventName",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "FinishingWeekendStartOffset",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "FirstCrewStartOffset",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "GateOpeningDate",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "PreEventWeekStartOffset",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "SetupWeekStartOffset",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "StrikeEndOffset",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "event_settings");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "event_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BarriosEarlyEntryAllocation",
                table: "event_settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BuildStartOffset",
                table: "event_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EarlyEntryCapacity",
                table: "event_settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Instant>(
                name: "EarlyEntryClose",
                table: "event_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EventEndOffset",
                table: "event_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EventName",
                table: "event_settings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

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

            migrationBuilder.AddColumn<LocalDate>(
                name: "GateOpeningDate",
                table: "event_settings",
                type: "date",
                nullable: false,
                defaultValue: new NodaTime.LocalDate(1, 1, 1));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "event_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

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

            migrationBuilder.AddColumn<int>(
                name: "StrikeEndOffset",
                table: "event_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "event_settings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "event_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_event_settings_IsActive",
                table: "event_settings",
                column: "IsActive");
        }
    }
}
