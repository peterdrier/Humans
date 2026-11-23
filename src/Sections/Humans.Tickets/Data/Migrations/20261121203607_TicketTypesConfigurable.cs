using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketTypesConfigurable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "DonationPurposes",
                table: "ticket_sales_events",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "child_types",
                table: "ticket_sales_events",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DonationPurposes",
                table: "ticket_sales_events");

            migrationBuilder.DropColumn(
                name: "child_types",
                table: "ticket_sales_events");
        }
    }
}
