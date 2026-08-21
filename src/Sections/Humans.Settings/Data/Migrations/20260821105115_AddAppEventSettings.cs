using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Settings.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppEventSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_event_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GateOpeningDate = table.Column<LocalDate>(type: "date", nullable: false),
                    BuildStartOffset = table.Column<int>(type: "integer", nullable: false),
                    EventEndOffset = table.Column<int>(type: "integer", nullable: false),
                    StrikeEndOffset = table.Column<int>(type: "integer", nullable: false),
                    FirstCrewStartOffset = table.Column<int>(type: "integer", nullable: false),
                    SetupWeekStartOffset = table.Column<int>(type: "integer", nullable: false),
                    PreEventWeekStartOffset = table.Column<int>(type: "integer", nullable: false),
                    FinishingWeekendStartOffset = table.Column<int>(type: "integer", nullable: false),
                    EarlyEntryCapacity = table.Column<string>(type: "jsonb", nullable: false),
                    BarriosEarlyEntryAllocation = table.Column<string>(type: "jsonb", nullable: true),
                    EarlyEntryClose = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_event_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_event_settings_IsActive",
                table: "app_event_settings",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_event_settings");
        }
    }
}
