using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Surveys.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRankedChoiceVoting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAsociadoVote",
                table: "surveys",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RankedSettings",
                table: "survey_questions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RankedUnavailableOptionValues",
                table: "survey_questions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RankedValue",
                table: "survey_answers",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAsociadoVote",
                table: "surveys");

            migrationBuilder.DropColumn(
                name: "RankedSettings",
                table: "survey_questions");

            migrationBuilder.DropColumn(
                name: "RankedUnavailableOptionValues",
                table: "survey_questions");

            migrationBuilder.DropColumn(
                name: "RankedValue",
                table: "survey_answers");
        }
    }
}
