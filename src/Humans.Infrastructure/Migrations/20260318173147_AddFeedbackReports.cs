using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackReports : Migration
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
                    ScreenshotFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ScreenshotStoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ScreenshotContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AdminNotes = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    GitHubIssueNumber = table.Column<int>(type: "integer", nullable: true),
                    AdminResponseSentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback_reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feedback_reports_users_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_feedback_reports_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_CreatedAt",
                table: "feedback_reports",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_ResolvedByUserId",
                table: "feedback_reports",
                column: "ResolvedByUserId");

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
                name: "feedback_reports");
        }
    }
}
