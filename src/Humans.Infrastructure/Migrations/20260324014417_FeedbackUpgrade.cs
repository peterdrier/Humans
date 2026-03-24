using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FeedbackUpgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Migrate existing admin notes to feedback_messages before dropping column
            migrationBuilder.Sql("""
                INSERT INTO feedback_messages (id, feedback_report_id, sender_user_id, content, created_at)
                SELECT gen_random_uuid(), id, COALESCE(resolved_by_user_id, user_id), admin_notes, updated_at
                FROM feedback_reports
                WHERE admin_notes IS NOT NULL AND admin_notes <> ''
                """);

            migrationBuilder.Sql("""
                UPDATE feedback_reports SET last_admin_message_at = updated_at
                WHERE admin_notes IS NOT NULL AND admin_notes <> ''
                """);

            migrationBuilder.DropColumn(
                name: "AdminNotes",
                table: "feedback_reports");

            migrationBuilder.DropColumn(
                name: "AdminResponseSentAt",
                table: "feedback_reports");

            migrationBuilder.AddColumn<Instant>(
                name: "LastReporterMessageAt",
                table: "feedback_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdditionalContext",
                table: "feedback_reports",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "LastAdminMessageAt",
                table: "feedback_reports",
                type: "timestamp with time zone",
                nullable: true);

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
                    table.ForeignKey(
                        name: "FK_feedback_messages_users_SenderUserId",
                        column: x => x.SenderUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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
                name: "IX_feedback_messages_SenderUserId",
                table: "feedback_messages",
                column: "SenderUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feedback_messages");

            migrationBuilder.DropColumn(
                name: "AdditionalContext",
                table: "feedback_reports");

            migrationBuilder.DropColumn(
                name: "LastAdminMessageAt",
                table: "feedback_reports");

            migrationBuilder.DropColumn(
                name: "LastReporterMessageAt",
                table: "feedback_reports");

            migrationBuilder.AddColumn<Instant>(
                name: "AdminResponseSentAt",
                table: "feedback_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdminNotes",
                table: "feedback_reports",
                type: "character varying(5000)",
                maxLength: 5000,
                nullable: true);
        }
    }
}
