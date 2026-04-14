using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestrictGoogleResourceTeamDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources");

            migrationBuilder.AddForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources",
                column: "TeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources");

            migrationBuilder.AddForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources",
                column: "TeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
