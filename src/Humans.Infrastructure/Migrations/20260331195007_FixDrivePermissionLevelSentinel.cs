using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixDrivePermissionLevelSentinel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Change column default from "Contributor" to "None" to match new CLR default
            migrationBuilder.AlterColumn<string>(
                name: "DrivePermissionLevel",
                table: "google_resources",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValue: "Contributor");

            // Set Group resources to "None" — they don't use permission levels
            migrationBuilder.Sql(
                """UPDATE google_resources SET "DrivePermissionLevel" = 'None' WHERE "ResourceType" = 'Group'""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DrivePermissionLevel",
                table: "google_resources",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Contributor",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValue: "None");

            // Restore Group resources to "Contributor" default
            migrationBuilder.Sql(
                """UPDATE google_resources SET "DrivePermissionLevel" = 'Contributor' WHERE "ResourceType" = 'Group'""");
        }
    }
}
