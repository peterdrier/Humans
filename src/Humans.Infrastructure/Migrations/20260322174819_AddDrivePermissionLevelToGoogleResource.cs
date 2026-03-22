using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDrivePermissionLevelToGoogleResource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DrivePermissionLevel",
                table: "google_resources",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Contributor");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DrivePermissionLevel",
                table: "google_resources");
        }
    }
}
