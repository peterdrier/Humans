using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.GoogleIntegration.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceIdentityDriveEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DriveEmail",
                table: "google_workspace_identities",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DriveEmail",
                table: "google_workspace_identities");
        }
    }
}
