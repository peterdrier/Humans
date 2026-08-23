using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Surveys.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSurveyInvitationEmailCopy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvitationEmailMessage",
                table: "surveys",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "InvitationEmailSubject",
                table: "surveys",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvitationEmailMessage",
                table: "surveys");

            migrationBuilder.DropColumn(
                name: "InvitationEmailSubject",
                table: "surveys");
        }
    }
}
