using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPreProdIntegrityAndGoogleSyncOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "google_sync_outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    DeduplicationKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_sync_outbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_google_sync_outbox_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_google_sync_outbox_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_role_assignments_valid_window",
                table: "role_assignments",
                sql: "\"ValidTo\" IS NULL OR \"ValidTo\" > \"ValidFrom\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_google_resources_exactly_one_owner",
                table: "google_resources",
                sql: "(\"TeamId\" IS NOT NULL AND \"UserId\" IS NULL) OR (\"TeamId\" IS NULL AND \"UserId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_google_sync_outbox_DeduplicationKey",
                table: "google_sync_outbox",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_google_sync_outbox_ProcessedAt_OccurredAt",
                table: "google_sync_outbox",
                columns: new[] { "ProcessedAt", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_google_sync_outbox_TeamId_UserId_ProcessedAt",
                table: "google_sync_outbox",
                columns: new[] { "TeamId", "UserId", "ProcessedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_google_sync_outbox_UserId",
                table: "google_sync_outbox",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "google_sync_outbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_role_assignments_valid_window",
                table: "role_assignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_google_resources_exactly_one_owner",
                table: "google_resources");
        }
    }
}
