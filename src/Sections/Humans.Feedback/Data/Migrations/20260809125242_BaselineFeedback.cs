using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Feedback.Data.Migrations
{
    /// <inheritdoc />
    public partial class BaselineFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feedback_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    PageUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AdditionalContext = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ScreenshotFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ScreenshotStoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ScreenshotContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    GitHubIssueNumber = table.Column<int>(type: "integer", nullable: true),
                    LastReporterMessageAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastAdminMessageAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValueSql: "'UserReport'"),
                    AgentConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedToTeamId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback_reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "feedback_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FeedbackReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Content = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feedback_messages_feedback_reports_FeedbackReportId",
                        column: x => x.FeedbackReportId,
                        principalTable: "feedback_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_messages_CreatedAt",
                table: "feedback_messages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_messages_FeedbackReportId",
                table: "feedback_messages",
                column: "FeedbackReportId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_AgentConversationId",
                table: "feedback_reports",
                column: "AgentConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_AssignedToTeamId",
                table: "feedback_reports",
                column: "AssignedToTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_AssignedToUserId",
                table: "feedback_reports",
                column: "AssignedToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_CreatedAt",
                table: "feedback_reports",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_Source",
                table: "feedback_reports",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_Status",
                table: "feedback_reports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_UserId",
                table: "feedback_reports",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feedback_messages");

            migrationBuilder.DropTable(
                name: "feedback_reports");
        }
    }
}
