using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketResaleOpensByHand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirectedResaleDays",
                table: "ticket_sales_events");

            migrationBuilder.DropColumn(
                name: "ResaleOpensAtSold",
                table: "ticket_sales_events");

            migrationBuilder.DropColumn(
                name: "DirectedExpiresAt",
                table: "ticket_resale_offers");

            migrationBuilder.AddColumn<Instant>(
                name: "ResaleOpenedAt",
                table: "ticket_sales_events",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResaleOpenedAt",
                table: "ticket_sales_events");

            migrationBuilder.AddColumn<int>(
                name: "DirectedResaleDays",
                table: "ticket_sales_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ResaleOpensAtSold",
                table: "ticket_sales_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "DirectedExpiresAt",
                table: "ticket_resale_offers",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
