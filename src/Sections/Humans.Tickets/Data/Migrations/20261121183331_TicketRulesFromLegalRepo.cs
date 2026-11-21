using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketRulesFromLegalRepo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TermsUrl",
                table: "ticket_sales_events");

            migrationBuilder.AddColumn<string>(
                name: "TermsFolder",
                table: "ticket_sales_events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsFolder",
                table: "ticket_checkout_orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsVersion",
                table: "ticket_checkout_orders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TermsFolder",
                table: "ticket_sales_events");

            migrationBuilder.DropColumn(
                name: "TermsFolder",
                table: "ticket_checkout_orders");

            migrationBuilder.DropColumn(
                name: "TermsVersion",
                table: "ticket_checkout_orders");

            migrationBuilder.AddColumn<string>(
                name: "TermsUrl",
                table: "ticket_sales_events",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }
    }
}
