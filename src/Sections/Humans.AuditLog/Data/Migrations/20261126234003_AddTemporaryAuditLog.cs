using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.AuditLog.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTemporaryAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log_temporary_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedEntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log_temporary_entries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_temporary_entries_Action",
                table: "audit_log_temporary_entries",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_temporary_entries_ActorUserId",
                table: "audit_log_temporary_entries",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_temporary_entries_EntityType_EntityId",
                table: "audit_log_temporary_entries",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_temporary_entries_OccurredAt",
                table: "audit_log_temporary_entries",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_temporary_entries_RelatedEntityType_RelatedEntity~",
                table: "audit_log_temporary_entries",
                columns: new[] { "RelatedEntityType", "RelatedEntityId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log_temporary_entries");
        }
    }
}
