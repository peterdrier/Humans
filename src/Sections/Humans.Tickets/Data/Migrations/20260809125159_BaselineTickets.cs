using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class BaselineTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BuyerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BuyerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    MatchedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TotalAmount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DiscountCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PaymentStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VendorEventId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    VendorDashboardUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PurchasedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    SyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    StripePaymentIntentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PaymentMethod = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PaymentMethodDetail = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    StripeFee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    ApplicationFee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    DiscountAmount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    DonationAmount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    VatAmount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_sync_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VendorEventId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastSyncAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    SyncStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StatusChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_sync_state", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_attendees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorTicketId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TicketOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttendeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AttendeeEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Barcode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MatchedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TicketTypeName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CheckedInAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    VendorEventId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_attendees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticket_attendees_ticket_orders_TicketOrderId",
                        column: x => x.TicketOrderId,
                        principalTable: "ticket_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_transfer_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalTicketAttendeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiverUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiverLegalName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ReceiverEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    SenderReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VendorResult = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VendorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NewVendorTicketId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    VendorHoldId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    VendorStepsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AdminNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_transfer_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticket_transfer_requests_ticket_attendees_OriginalTicketAtt~",
                        column: x => x.OriginalTicketAttendeeId,
                        principalTable: "ticket_attendees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "ticket_sync_state",
                columns: new[] { "Id", "LastError", "LastSyncAt", "StatusChangedAt", "SyncStatus", "VendorEventId" },
                values: new object[] { 1, null, null, null, "Idle", "" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_attendees_AttendeeEmail",
                table: "ticket_attendees",
                column: "AttendeeEmail");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_attendees_Barcode",
                table: "ticket_attendees",
                column: "Barcode");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_attendees_MatchedUserId",
                table: "ticket_attendees",
                column: "MatchedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_attendees_TicketOrderId",
                table: "ticket_attendees",
                column: "TicketOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_attendees_VendorTicketId",
                table: "ticket_attendees",
                column: "VendorTicketId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_orders_BuyerEmail",
                table: "ticket_orders",
                column: "BuyerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_orders_MatchedUserId",
                table: "ticket_orders",
                column: "MatchedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_orders_PaymentMethod",
                table: "ticket_orders",
                column: "PaymentMethod");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_orders_PurchasedAt",
                table: "ticket_orders",
                column: "PurchasedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_orders_VendorOrderId",
                table: "ticket_orders",
                column: "VendorOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_transfer_requests_OriginalTicketAttendeeId",
                table: "ticket_transfer_requests",
                column: "OriginalTicketAttendeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_transfer_requests_SenderUserId_Status",
                table: "ticket_transfer_requests",
                columns: new[] { "SenderUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_transfer_requests_Status",
                table: "ticket_transfer_requests",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_sync_state");

            migrationBuilder.DropTable(
                name: "ticket_transfer_requests");

            migrationBuilder.DropTable(
                name: "ticket_attendees");

            migrationBuilder.DropTable(
                name: "ticket_orders");
        }
    }
}
