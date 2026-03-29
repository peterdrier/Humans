using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeFeeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ApplicationFee",
                table: "ticket_orders",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "ticket_orders",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "ticket_orders",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethodDetail",
                table: "ticket_orders",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StripeFee",
                table: "ticket_orders",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripePaymentIntentId",
                table: "ticket_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_orders_PaymentMethod",
                table: "ticket_orders",
                column: "PaymentMethod");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ticket_orders_PaymentMethod",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "ApplicationFee",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "PaymentMethodDetail",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "StripeFee",
                table: "ticket_orders");

            migrationBuilder.DropColumn(
                name: "StripePaymentIntentId",
                table: "ticket_orders");
        }
    }
}
