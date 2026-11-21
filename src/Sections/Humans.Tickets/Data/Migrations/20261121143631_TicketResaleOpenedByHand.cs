using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketResaleOpenedByHand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}
