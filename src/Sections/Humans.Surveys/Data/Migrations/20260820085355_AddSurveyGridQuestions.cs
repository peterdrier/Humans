using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Surveys.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSurveyGridQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GridRows",
                table: "survey_questions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GridSelectionMode",
                table: "survey_questions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GridSelections",
                table: "survey_answers",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GridRows",
                table: "survey_questions");

            migrationBuilder.DropColumn(
                name: "GridSelectionMode",
                table: "survey_questions");

            migrationBuilder.DropColumn(
                name: "GridSelections",
                table: "survey_answers");
        }
    }
}
