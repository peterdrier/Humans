using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_outbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    RecipientName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    HtmlBody = table.Column<string>(type: "text", nullable: false),
                    PlainTextBody = table.Column<string>(type: "text", nullable: true),
                    TemplateName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CampaignGrantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReplyTo = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ExtraHeaders = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    PickedUpAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    NextRetryAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_outbox_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_email_outbox_messages_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_outbox_messages_CampaignGrantId",
                table: "email_outbox_messages",
                column: "CampaignGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_email_outbox_messages_SentAt_RetryCount_NextRetryAt_PickedU~",
                table: "email_outbox_messages",
                columns: new[] { "SentAt", "RetryCount", "NextRetryAt", "PickedUpAt" });

            migrationBuilder.CreateIndex(
                name: "IX_email_outbox_messages_UserId",
                table: "email_outbox_messages",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_outbox_messages");
        }
    }
}
