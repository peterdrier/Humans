using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedToTeamId",
                table: "feedback_reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedToUserId",
                table: "feedback_reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_AssignedToTeamId",
                table: "feedback_reports",
                column: "AssignedToTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_AssignedToUserId",
                table: "feedback_reports",
                column: "AssignedToUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_reports_teams_AssignedToTeamId",
                table: "feedback_reports",
                column: "AssignedToTeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_reports_users_AssignedToUserId",
                table: "feedback_reports",
                column: "AssignedToUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_feedback_reports_teams_AssignedToTeamId",
                table: "feedback_reports");

            migrationBuilder.DropForeignKey(
                name: "FK_feedback_reports_users_AssignedToUserId",
                table: "feedback_reports");

            migrationBuilder.DropIndex(
                name: "IX_feedback_reports_AssignedToTeamId",
                table: "feedback_reports");

            migrationBuilder.DropIndex(
                name: "IX_feedback_reports_AssignedToUserId",
                table: "feedback_reports");

            migrationBuilder.DropColumn(
                name: "AssignedToTeamId",
                table: "feedback_reports");

            migrationBuilder.DropColumn(
                name: "AssignedToUserId",
                table: "feedback_reports");
        }
    }
}
