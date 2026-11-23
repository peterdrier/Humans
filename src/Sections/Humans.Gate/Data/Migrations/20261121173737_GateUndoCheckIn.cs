using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Gate.Data.Migrations
{
    /// <inheritdoc />
    public partial class GateUndoCheckIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UndoReason",
                table: "gate_scan_events",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "UndoneAt",
                table: "gate_scan_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UndoneByUserId",
                table: "gate_scan_events",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UndoReason",
                table: "gate_scan_events");

            migrationBuilder.DropColumn(
                name: "UndoneAt",
                table: "gate_scan_events");

            migrationBuilder.DropColumn(
                name: "UndoneByUserId",
                table: "gate_scan_events");
        }
    }
}
