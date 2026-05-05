using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketTransferRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_transfer_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalTicketAttendeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RecipientEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    RequesterReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VendorResult = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VendorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NewVendorTicketId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
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

            migrationBuilder.CreateIndex(
                name: "IX_ticket_transfer_requests_OriginalTicketAttendeeId",
                table: "ticket_transfer_requests",
                column: "OriginalTicketAttendeeId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_transfer_requests_RequesterUserId_Status",
                table: "ticket_transfer_requests",
                columns: new[] { "RequesterUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_transfer_requests_Status",
                table: "ticket_transfer_requests",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_transfer_requests");
        }
    }
}
