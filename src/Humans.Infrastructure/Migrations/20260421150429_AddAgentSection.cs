using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentConversationId",
                table: "feedback_reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "feedback_reports",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "UserReport");

            migrationBuilder.CreateTable(
                name: "agent_conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Locale = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MessageCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_conversations_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_rate_limits",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<LocalDate>(type: "date", nullable: false),
                    MessagesToday = table.Column<int>(type: "integer", nullable: false),
                    TokensToday = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_rate_limits", x => new { x.UserId, x.Day });
                    table.ForeignKey(
                        name: "FK_agent_rate_limits_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreloadConfig = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DailyMessageCap = table.Column<int>(type: "integer", nullable: false),
                    HourlyMessageCap = table.Column<int>(type: "integer", nullable: false),
                    DailyTokenCap = table.Column<int>(type: "integer", nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_settings", x => x.Id);
                });

            migrationBuilder.Sql(
                """
                ALTER TABLE agent_settings
                    ADD CONSTRAINT ck_agent_settings_singleton CHECK (id = 1);
                """);

            migrationBuilder.CreateTable(
                name: "agent_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    CachedTokens = table.Column<int>(type: "integer", nullable: false),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    FetchedDocs = table.Column<string>(type: "jsonb", nullable: false),
                    RefusalReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    HandedOffToFeedbackId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_messages_agent_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "agent_conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_messages_feedback_reports_HandedOffToFeedbackId",
                        column: x => x.HandedOffToFeedbackId,
                        principalTable: "feedback_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.InsertData(
                table: "agent_settings",
                columns: new[] { "Id", "DailyMessageCap", "DailyTokenCap", "Enabled", "HourlyMessageCap", "Model", "PreloadConfig", "RetentionDays", "UpdatedAt" },
                values: new object[] { 1, 30, 50000, false, 10, "claude-sonnet-4-6", "Tier1", 90, NodaTime.Instant.FromUnixTimeTicks(17767296000000000L) });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_AgentConversationId",
                table: "feedback_reports",
                column: "AgentConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_reports_Source",
                table: "feedback_reports",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_agent_conversations_LastMessageAt",
                table: "agent_conversations",
                column: "LastMessageAt");

            migrationBuilder.CreateIndex(
                name: "IX_agent_conversations_UserId",
                table: "agent_conversations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_messages_ConversationId",
                table: "agent_messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_messages_CreatedAt",
                table: "agent_messages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_agent_messages_HandedOffToFeedbackId",
                table: "agent_messages",
                column: "HandedOffToFeedbackId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_messages_RefusalReason",
                table: "agent_messages",
                column: "RefusalReason");

            migrationBuilder.CreateIndex(
                name: "IX_agent_rate_limits_Day",
                table: "agent_rate_limits",
                column: "Day");

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_reports_agent_conversations_AgentConversationId",
                table: "feedback_reports",
                column: "AgentConversationId",
                principalTable: "agent_conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_feedback_reports_agent_conversations_AgentConversationId",
                table: "feedback_reports");

            migrationBuilder.DropTable(
                name: "agent_messages");

            migrationBuilder.DropTable(
                name: "agent_rate_limits");

            migrationBuilder.Sql("ALTER TABLE agent_settings DROP CONSTRAINT IF EXISTS ck_agent_settings_singleton;");

            migrationBuilder.DropTable(
                name: "agent_settings");

            migrationBuilder.DropTable(
                name: "agent_conversations");

            migrationBuilder.DropIndex(
                name: "IX_feedback_reports_AgentConversationId",
                table: "feedback_reports");

            migrationBuilder.DropIndex(
                name: "IX_feedback_reports_Source",
                table: "feedback_reports");

            migrationBuilder.DropColumn(
                name: "AgentConversationId",
                table: "feedback_reports");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "feedback_reports");
        }
    }
}
