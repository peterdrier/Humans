using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Store.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreOrderCheckoutAttempt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CheckoutAmountEur",
                table: "store_orders",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CheckoutAttemptId",
                table: "store_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckoutSessionId",
                table: "store_orders",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckoutAmountEur",
                table: "store_orders");

            migrationBuilder.DropColumn(
                name: "CheckoutAttemptId",
                table: "store_orders");

            migrationBuilder.DropColumn(
                name: "CheckoutSessionId",
                table: "store_orders");
        }
    }
}
