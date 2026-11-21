using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketEventCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "ticket_sales_events",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "CancelledAt",
                table: "ticket_sales_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledByUserId",
                table: "ticket_sales_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "CancellationEmailedAt",
                table: "ticket_checkout_orders",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "ticket_sales_events");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "ticket_sales_events");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "ticket_sales_events");

            migrationBuilder.DropColumn(
                name: "CancellationEmailedAt",
                table: "ticket_checkout_orders");
        }
    }
}
