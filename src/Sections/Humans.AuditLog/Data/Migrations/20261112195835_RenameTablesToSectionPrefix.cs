using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.AuditLog.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_audit_log",
                table: "audit_log");

            migrationBuilder.RenameTable(
                name: "audit_log",
                newName: "audit_log_entries");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_ResourceId",
                table: "audit_log_entries",
                newName: "IX_audit_log_entries_ResourceId");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_RelatedEntityType_RelatedEntityId",
                table: "audit_log_entries",
                newName: "IX_audit_log_entries_RelatedEntityType_RelatedEntityId");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_OccurredAt",
                table: "audit_log_entries",
                newName: "IX_audit_log_entries_OccurredAt");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_EntityType_EntityId",
                table: "audit_log_entries",
                newName: "IX_audit_log_entries_EntityType_EntityId");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_Action",
                table: "audit_log_entries",
                newName: "IX_audit_log_entries_Action");

            migrationBuilder.AddPrimaryKey(
                name: "PK_audit_log_entries",
                table: "audit_log_entries",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_audit_log_entries",
                table: "audit_log_entries");

            migrationBuilder.RenameTable(
                name: "audit_log_entries",
                newName: "audit_log");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_entries_ResourceId",
                table: "audit_log",
                newName: "IX_audit_log_ResourceId");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_entries_RelatedEntityType_RelatedEntityId",
                table: "audit_log",
                newName: "IX_audit_log_RelatedEntityType_RelatedEntityId");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_entries_OccurredAt",
                table: "audit_log",
                newName: "IX_audit_log_OccurredAt");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_entries_EntityType_EntityId",
                table: "audit_log",
                newName: "IX_audit_log_EntityType_EntityId");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_entries_Action",
                table: "audit_log",
                newName: "IX_audit_log_Action");

            migrationBuilder.AddPrimaryKey(
                name: "PK_audit_log",
                table: "audit_log",
                column: "Id");
        }
    }
}
