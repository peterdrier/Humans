using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Surveys.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSurveyQuestionComment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Comment",
                table: "survey_questions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Comment",
                table: "survey_questions");
        }
    }
}
