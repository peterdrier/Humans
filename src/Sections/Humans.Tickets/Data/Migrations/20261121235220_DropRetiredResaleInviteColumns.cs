using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropRetiredResaleInviteColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ticket_resale_offers_DirectedTokenHash",
                table: "ticket_resale_offers");

            migrationBuilder.DropColumn(
                name: "DirectedEmail",
                table: "ticket_resale_offers");

            migrationBuilder.DropColumn(
                name: "DirectedTokenHash",
                table: "ticket_resale_offers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DirectedEmail",
                table: "ticket_resale_offers",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectedTokenHash",
                table: "ticket_resale_offers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_resale_offers_DirectedTokenHash",
                table: "ticket_resale_offers",
                column: "DirectedTokenHash");
        }
    }
}
