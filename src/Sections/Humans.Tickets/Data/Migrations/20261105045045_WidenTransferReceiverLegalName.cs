using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WidenTransferReceiverLegalName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ReceiverLegalName",
                table: "ticket_transfer_requests",
                type: "character varying(513)",
                maxLength: 513,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ReceiverLegalName",
                table: "ticket_transfer_requests",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(513)",
                oldMaxLength: 513);
        }
    }
}
