using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Gate.Data.Migrations
{
    /// <inheritdoc />
    public partial class GateCheckoutTicketsAndRequirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CheckoutTicketId",
                table: "gate_scan_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmedRequirements",
                table: "gate_scan_events",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckoutTicketId",
                table: "gate_scan_events");

            migrationBuilder.DropColumn(
                name: "ConfirmedRequirements",
                table: "gate_scan_events");
        }
    }
}
