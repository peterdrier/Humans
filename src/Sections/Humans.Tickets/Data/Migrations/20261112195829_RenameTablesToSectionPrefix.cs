using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_tickets_event_participations",
                table: "tickets_event_participations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ticket_sync_state",
                table: "ticket_sync_state");

            migrationBuilder.RenameTable(
                name: "tickets_event_participations",
                newName: "ticket_event_participations");

            migrationBuilder.RenameTable(
                name: "ticket_sync_state",
                newName: "ticket_sync_states");

            migrationBuilder.RenameIndex(
                name: "IX_tickets_event_participations_UserId_Year",
                table: "ticket_event_participations",
                newName: "IX_ticket_event_participations_UserId_Year");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ticket_event_participations",
                table: "ticket_event_participations",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ticket_sync_states",
                table: "ticket_sync_states",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ticket_sync_states",
                table: "ticket_sync_states");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ticket_event_participations",
                table: "ticket_event_participations");

            migrationBuilder.RenameTable(
                name: "ticket_sync_states",
                newName: "ticket_sync_state");

            migrationBuilder.RenameTable(
                name: "ticket_event_participations",
                newName: "tickets_event_participations");

            migrationBuilder.RenameIndex(
                name: "IX_ticket_event_participations_UserId_Year",
                table: "tickets_event_participations",
                newName: "IX_tickets_event_participations_UserId_Year");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ticket_sync_state",
                table: "ticket_sync_state",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_tickets_event_participations",
                table: "tickets_event_participations",
                column: "Id");
        }
    }
}
