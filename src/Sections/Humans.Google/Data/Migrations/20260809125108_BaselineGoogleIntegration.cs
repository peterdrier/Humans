using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Humans.GoogleIntegration.Data.Migrations
{
    /// <inheritdoc />
    public partial class BaselineGoogleIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "google_resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceType = table.Column<string>(type: "text", nullable: false),
                    GoogleId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProvisionedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DrivePermissionLevel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RestrictInheritedAccess = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_resources", x => x.Id);
                });

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
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    DeduplicationKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    FailedPermanently = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_sync_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sync_service_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SyncMode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_service_settings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "sync_service_settings",
                columns: new[] { "Id", "ServiceType", "SyncMode", "UpdatedAt", "UpdatedByUserId" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0002-000000000001"), "GoogleDrive", "None", NodaTime.Instant.FromUnixTimeTicks(17730144000000000L), null },
                    { new Guid("00000000-0000-0000-0002-000000000002"), "GoogleGroups", "None", NodaTime.Instant.FromUnixTimeTicks(17730144000000000L), null },
                    { new Guid("00000000-0000-0000-0002-000000000003"), "Discord", "None", NodaTime.Instant.FromUnixTimeTicks(17730144000000000L), null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_google_resources_GoogleId",
                table: "google_resources",
                column: "GoogleId");

            migrationBuilder.CreateIndex(
                name: "IX_google_resources_IsActive",
                table: "google_resources",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_google_resources_TeamId",
                table: "google_resources",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_google_resources_TeamId_GoogleId",
                table: "google_resources",
                columns: new[] { "TeamId", "GoogleId" },
                unique: true,
                filter: "\"IsActive\" = true");

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
                name: "IX_sync_service_settings_ServiceType",
                table: "sync_service_settings",
                column: "ServiceType",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "google_resources");

            migrationBuilder.DropTable(
                name: "google_sync_outbox");

            migrationBuilder.DropTable(
                name: "sync_service_settings");
        }
    }
}
