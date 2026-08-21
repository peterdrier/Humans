using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Settings.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameSystemSettingsTableToSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_system_settings",
                table: "system_settings");

            migrationBuilder.RenameTable(
                name: "system_settings",
                newName: "settings");

            migrationBuilder.AddPrimaryKey(
                name: "PK_settings",
                table: "settings",
                column: "Key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_settings",
                table: "settings");

            migrationBuilder.RenameTable(
                name: "settings",
                newName: "system_settings");

            migrationBuilder.AddPrimaryKey(
                name: "PK_system_settings",
                table: "system_settings",
                column: "Key");
        }
    }
}
