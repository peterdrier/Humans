using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Store.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreInvoiceBookingAndKeptDeposits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StripeGatewayTreasuryId",
                table: "store_settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                table: "store_payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingNote",
                table: "store_invoices",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BookingState",
                table: "store_invoices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "SentAt",
                table: "store_invoices",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StripeGatewayTreasuryId",
                table: "store_settings");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "store_payments");

            migrationBuilder.DropColumn(
                name: "BookingNote",
                table: "store_invoices");

            migrationBuilder.DropColumn(
                name: "BookingState",
                table: "store_invoices");

            migrationBuilder.DropColumn(
                name: "SentAt",
                table: "store_invoices");
        }
    }
}
