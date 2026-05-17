using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropCrossDomainTeamFkConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_budget_line_items_teams_ResponsibleTeamId",
                table: "budget_line_items");

            migrationBuilder.DropForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources");

            migrationBuilder.DropForeignKey(
                name: "FK_legal_documents_teams_TeamId",
                table: "legal_documents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_budget_line_items_teams_ResponsibleTeamId",
                table: "budget_line_items",
                column: "ResponsibleTeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_google_resources_teams_TeamId",
                table: "google_resources",
                column: "TeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_legal_documents_teams_TeamId",
                table: "legal_documents",
                column: "TeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
