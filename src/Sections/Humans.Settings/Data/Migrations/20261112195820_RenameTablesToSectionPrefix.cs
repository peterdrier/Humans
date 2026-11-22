using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Settings.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_system_settings",
                table: "system_settings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_settings_event",
                table: "settings_event");

            migrationBuilder.RenameTable(
                name: "system_settings",
                newName: "settings_system_values");

            migrationBuilder.RenameTable(
                name: "settings_event",
                newName: "settings_events");

            migrationBuilder.RenameIndex(
                name: "IX_settings_event_Status",
                table: "settings_events",
                newName: "IX_settings_events_Status");

            migrationBuilder.AddPrimaryKey(
                name: "PK_settings_system_values",
                table: "settings_system_values",
                column: "Key");

            migrationBuilder.AddPrimaryKey(
                name: "PK_settings_events",
                table: "settings_events",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_settings_system_values",
                table: "settings_system_values");

            migrationBuilder.DropPrimaryKey(
                name: "PK_settings_events",
                table: "settings_events");

            migrationBuilder.RenameTable(
                name: "settings_system_values",
                newName: "system_settings");

            migrationBuilder.RenameTable(
                name: "settings_events",
                newName: "settings_event");

            migrationBuilder.RenameIndex(
                name: "IX_settings_events_Status",
                table: "settings_event",
                newName: "IX_settings_event_Status");

            migrationBuilder.AddPrimaryKey(
                name: "PK_system_settings",
                table: "system_settings",
                column: "Key");

            migrationBuilder.AddPrimaryKey(
                name: "PK_settings_event",
                table: "settings_event",
                column: "Id");
        }
    }
}
