using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Settings.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSettingsEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "settings_event",
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
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings_event", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_settings_event_Status",
                table: "settings_event",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "settings_event");
        }
    }
}
