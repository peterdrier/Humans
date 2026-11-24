using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Audit.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubjectTargetJobName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RelatedEntityType",
                table: "audit_log_entries",
                newName: "TargetType");

            migrationBuilder.RenameColumn(
                name: "RelatedEntityId",
                table: "audit_log_entries",
                newName: "TargetId");

            migrationBuilder.RenameColumn(
                name: "EntityType",
                table: "audit_log_entries",
                newName: "SubjectType");

            migrationBuilder.RenameColumn(
                name: "EntityId",
                table: "audit_log_entries",
                newName: "SubjectId");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_entries_RelatedEntityType_RelatedEntityId",
                table: "audit_log_entries",
                newName: "IX_audit_log_entries_TargetType_TargetId");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_entries_EntityType_EntityId",
                table: "audit_log_entries",
                newName: "IX_audit_log_entries_SubjectType_SubjectId");

            migrationBuilder.AddColumn<string>(
                name: "JobName",
                table: "audit_log_entries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JobName",
                table: "audit_log_entries");

            migrationBuilder.RenameColumn(
                name: "TargetType",
                table: "audit_log_entries",
                newName: "RelatedEntityType");

            migrationBuilder.RenameColumn(
                name: "TargetId",
                table: "audit_log_entries",
                newName: "RelatedEntityId");

            migrationBuilder.RenameColumn(
                name: "SubjectType",
                table: "audit_log_entries",
                newName: "EntityType");

            migrationBuilder.RenameColumn(
                name: "SubjectId",
                table: "audit_log_entries",
                newName: "EntityId");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_entries_TargetType_TargetId",
                table: "audit_log_entries",
                newName: "IX_audit_log_entries_RelatedEntityType_RelatedEntityId");

            migrationBuilder.RenameIndex(
                name: "IX_audit_log_entries_SubjectType_SubjectId",
                table: "audit_log_entries",
                newName: "IX_audit_log_entries_EntityType_EntityId");
        }
    }
}
