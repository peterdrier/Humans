using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Surveys.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSurveyAuthoringApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RejectionNote",
                table: "surveys",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "SubmittedAt",
                table: "surveys",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RejectionNote",
                table: "surveys");

            migrationBuilder.DropColumn(
                name: "SubmittedAt",
                table: "surveys");
        }
    }
}
