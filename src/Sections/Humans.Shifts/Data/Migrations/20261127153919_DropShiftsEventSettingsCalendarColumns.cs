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
                name: "IX_shift_event_settings_IsActive",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "BarriosEarlyEntryAllocation",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "BuildStartOffset",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "EarlyEntryCapacity",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "EarlyEntryClose",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "EventEndOffset",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "EventName",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "FinishingWeekendStartOffset",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "FirstCrewStartOffset",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "GateOpeningDate",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "PreEventWeekStartOffset",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "SetupWeekStartOffset",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "StrikeEndOffset",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "shift_event_settings");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "shift_event_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BarriosEarlyEntryAllocation",
                table: "shift_event_settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BuildStartOffset",
                table: "shift_event_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EarlyEntryCapacity",
                table: "shift_event_settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Instant>(
                name: "EarlyEntryClose",
                table: "shift_event_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EventEndOffset",
                table: "shift_event_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EventName",
                table: "shift_event_settings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "FinishingWeekendStartOffset",
                table: "shift_event_settings",
                type: "integer",
                nullable: false,
                defaultValue: -4);

            migrationBuilder.AddColumn<int>(
                name: "FirstCrewStartOffset",
                table: "shift_event_settings",
                type: "integer",
                nullable: false,
                defaultValue: -25);

            migrationBuilder.AddColumn<LocalDate>(
                name: "GateOpeningDate",
                table: "shift_event_settings",
                type: "date",
                nullable: false,
                defaultValue: new NodaTime.LocalDate(1, 1, 1));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "shift_event_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PreEventWeekStartOffset",
                table: "shift_event_settings",
                type: "integer",
                nullable: false,
                defaultValue: -9);

            migrationBuilder.AddColumn<int>(
                name: "SetupWeekStartOffset",
                table: "shift_event_settings",
                type: "integer",
                nullable: false,
                defaultValue: -16);

            migrationBuilder.AddColumn<int>(
                name: "StrikeEndOffset",
                table: "shift_event_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "shift_event_settings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "shift_event_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_shift_event_settings_IsActive",
                table: "shift_event_settings",
                column: "IsActive");
        }
    }
}
