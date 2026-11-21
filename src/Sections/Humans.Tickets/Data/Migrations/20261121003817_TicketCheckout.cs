using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketCheckout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_checkout_order_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_checkout_order_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_checkout_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuyerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuyerName = table.Column<string>(type: "character varying(513)", maxLength: 513, nullable: false),
                    BuyerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    BackendKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BackendOrderId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PaymentUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    AdminUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    TotalMinor = table.Column<long>(type: "bigint", nullable: false),
                    FeeMinor = table.Column<long>(type: "bigint", nullable: true),
                    PaymentMethod = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PaidAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RefundedMinor = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_checkout_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_checkout_refunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BackendRefundId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ManualRefundUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_checkout_refunds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_sales_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventSettingsId = table.Column<Guid>(type: "uuid", nullable: false),
                    BackendKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BackendEventRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SalesCloseAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    TicketVatRate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    SupporterTotalsMinor = table.Column<List<long>>(type: "bigint[]", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    price_rules = table.Column<string>(type: "jsonb", nullable: true),
                    waves = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_sales_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_checkout_order_events_OrderId",
                table: "ticket_checkout_order_events",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_checkout_orders_BackendKey_BackendOrderId",
                table: "ticket_checkout_orders",
                columns: new[] { "BackendKey", "BackendOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_checkout_orders_BuyerUserId",
                table: "ticket_checkout_orders",
                column: "BuyerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_checkout_orders_SalesEventId",
                table: "ticket_checkout_orders",
                column: "SalesEventId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_checkout_refunds_OrderId",
                table: "ticket_checkout_refunds",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_sales_events_EventSettingsId",
                table: "ticket_sales_events",
                column: "EventSettingsId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_checkout_order_events");

            migrationBuilder.DropTable(
                name: "ticket_checkout_orders");

            migrationBuilder.DropTable(
                name: "ticket_checkout_refunds");

            migrationBuilder.DropTable(
                name: "ticket_sales_events");
        }
    }
}
