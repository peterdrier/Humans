using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackTicketVoidIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "VoidRefundId",
                table: "ticket_checkout_tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VoidRefundMinor",
                table: "ticket_checkout_tickets",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidRequestedReason",
                table: "ticket_checkout_tickets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VoidRefundId",
                table: "ticket_checkout_tickets");

            migrationBuilder.DropColumn(
                name: "VoidRefundMinor",
                table: "ticket_checkout_tickets");

            migrationBuilder.DropColumn(
                name: "VoidRequestedReason",
                table: "ticket_checkout_tickets");
        }
    }
}
