using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropTicketTransferVendorFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NewVendorTicketId",
                table: "ticket_transfer_requests");

            migrationBuilder.DropColumn(
                name: "VendorMessage",
                table: "ticket_transfer_requests");

            migrationBuilder.DropColumn(
                name: "VendorResult",
                table: "ticket_transfer_requests");

            migrationBuilder.DropColumn(
                name: "VendorStepsJson",
                table: "ticket_transfer_requests");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NewVendorTicketId",
                table: "ticket_transfer_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VendorMessage",
                table: "ticket_transfer_requests",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VendorResult",
                table: "ticket_transfer_requests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VendorStepsJson",
                table: "ticket_transfer_requests",
                type: "text",
                nullable: false,
                defaultValue: "[]");
        }
    }
}
