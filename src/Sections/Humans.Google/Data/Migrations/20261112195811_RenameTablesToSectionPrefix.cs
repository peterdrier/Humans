using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.GoogleIntegration.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_sync_service_settings",
                table: "sync_service_settings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_google_sync_log",
                table: "google_sync_log");

            migrationBuilder.RenameTable(
                name: "sync_service_settings",
                newName: "google_sync_service_settings");

            migrationBuilder.RenameTable(
                name: "google_sync_log",
                newName: "google_sync_log_entries");

            migrationBuilder.RenameIndex(
                name: "IX_sync_service_settings_ServiceType",
                table: "google_sync_service_settings",
                newName: "IX_google_sync_service_settings_ServiceType");

            migrationBuilder.RenameIndex(
                name: "IX_google_sync_log_UserId",
                table: "google_sync_log_entries",
                newName: "IX_google_sync_log_entries_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_google_sync_log_ResourceId",
                table: "google_sync_log_entries",
                newName: "IX_google_sync_log_entries_ResourceId");

            migrationBuilder.RenameIndex(
                name: "IX_google_sync_log_OccurredAt",
                table: "google_sync_log_entries",
                newName: "IX_google_sync_log_entries_OccurredAt");

            migrationBuilder.AddPrimaryKey(
                name: "PK_google_sync_service_settings",
                table: "google_sync_service_settings",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_google_sync_log_entries",
                table: "google_sync_log_entries",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_google_sync_service_settings",
                table: "google_sync_service_settings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_google_sync_log_entries",
                table: "google_sync_log_entries");

            migrationBuilder.RenameTable(
                name: "google_sync_service_settings",
                newName: "sync_service_settings");

            migrationBuilder.RenameTable(
                name: "google_sync_log_entries",
                newName: "google_sync_log");

            migrationBuilder.RenameIndex(
                name: "IX_google_sync_service_settings_ServiceType",
                table: "sync_service_settings",
                newName: "IX_sync_service_settings_ServiceType");

            migrationBuilder.RenameIndex(
                name: "IX_google_sync_log_entries_UserId",
                table: "google_sync_log",
                newName: "IX_google_sync_log_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_google_sync_log_entries_ResourceId",
                table: "google_sync_log",
                newName: "IX_google_sync_log_ResourceId");

            migrationBuilder.RenameIndex(
                name: "IX_google_sync_log_entries_OccurredAt",
                table: "google_sync_log",
                newName: "IX_google_sync_log_OccurredAt");

            migrationBuilder.AddPrimaryKey(
                name: "PK_sync_service_settings",
                table: "sync_service_settings",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_google_sync_log",
                table: "google_sync_log",
                column: "Id");
        }
    }
}
