using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Gate.Data.Migrations
{
    /// <inheritdoc />
    public partial class GateCheckoutTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CheckoutTicketId",
                table: "gate_scan_events",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckoutTicketId",
                table: "gate_scan_events");
        }
    }
}
