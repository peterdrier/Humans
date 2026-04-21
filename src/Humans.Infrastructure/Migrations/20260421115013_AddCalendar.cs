using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LocationUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OwningTeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    IsAllDay = table.Column<bool>(type: "boolean", nullable: false),
                    RecurrenceRule = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecurrenceTimezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RecurrenceUntilUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_calendar_events_teams_OwningTeamId",
                        column: x => x.OwningTeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "calendar_event_exceptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalOccurrenceStartUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false),
                    OverrideStartUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    OverrideEndUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    OverrideTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OverrideDescription = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    OverrideLocation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OverrideLocationUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_event_exceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_calendar_event_exceptions_calendar_events_EventId",
                        column: x => x.EventId,
                        principalTable: "calendar_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_event_exceptions_EventId_OriginalOccurrenceStartUtc",
                table: "calendar_event_exceptions",
                columns: new[] { "EventId", "OriginalOccurrenceStartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_calendar_events_OwningTeamId_StartUtc",
                table: "calendar_events",
                columns: new[] { "OwningTeamId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_events_StartUtc_RecurrenceUntilUtc",
                table: "calendar_events",
                columns: new[] { "StartUtc", "RecurrenceUntilUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_event_exceptions");

            migrationBuilder.DropTable(
                name: "calendar_events");
        }
    }
}
