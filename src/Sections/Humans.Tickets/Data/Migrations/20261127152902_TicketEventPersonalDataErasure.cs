using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketEventPersonalDataErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PostEventHoldDays",
                table: "ticket_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "PersonalDataErasedAt",
                table: "ticket_sales_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "ticket_settings",
                keyColumn: "Id",
                keyValue: 1,
                column: "PostEventHoldDays",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PostEventHoldDays",
                table: "ticket_settings");

            migrationBuilder.DropColumn(
                name: "PersonalDataErasedAt",
                table: "ticket_sales_events");
        }
    }
}
