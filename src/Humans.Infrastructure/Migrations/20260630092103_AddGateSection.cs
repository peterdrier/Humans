using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGateSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gate_scan_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ScannedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Barcode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TicketAttendeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuestUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Verdict = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LaneId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ClientScanAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OverrideByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AdmitDedupeKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gate_scan_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "gate_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    GeneralEntryOpensAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    MinorAgeThresholdYears = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gate_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "gate_staff_pins",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PinHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gate_staff_pins", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gate_scan_events_admit_dedupe_key",
                table: "gate_scan_events",
                column: "AdmitDedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gate_scan_events_OccurredAt",
                table: "gate_scan_events",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_gate_scan_events_ScannedByUserId",
                table: "gate_scan_events",
                column: "ScannedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gate_scan_events");

            migrationBuilder.DropTable(
                name: "gate_settings");

            migrationBuilder.DropTable(
                name: "gate_staff_pins");
        }
    }
}
