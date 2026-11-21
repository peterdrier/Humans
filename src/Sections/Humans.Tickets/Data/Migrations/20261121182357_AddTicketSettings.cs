using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Tickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    IsScheduledSyncEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_settings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "ticket_settings",
                columns: new[] { "Id", "IsScheduledSyncEnabled" },
                values: new object[] { 1, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_settings");
        }
    }
}
