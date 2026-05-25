using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketTransferVendorStepsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VendorStepsJson",
                table: "ticket_transfer_requests",
                type: "text",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VendorStepsJson",
                table: "ticket_transfer_requests");
        }
    }
}
