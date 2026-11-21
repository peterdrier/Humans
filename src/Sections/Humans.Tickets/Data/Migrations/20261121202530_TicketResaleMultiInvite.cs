using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketResaleMultiInvite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "UnsoldNotifiedAt",
                table: "ticket_resale_offers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "directed_invites",
                table: "ticket_resale_offers",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnsoldNotifiedAt",
                table: "ticket_resale_offers");

            migrationBuilder.DropColumn(
                name: "directed_invites",
                table: "ticket_resale_offers");
        }
    }
}
