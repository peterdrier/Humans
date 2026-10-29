using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Camps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCampEventIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EventSettingsId",
                table: "camp_seasons",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "camp_event_settings",
                columns: table => new
                {
                    EventSettingsId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationOpen = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camp_event_settings", x => x.EventSettingsId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "camp_event_settings");

            migrationBuilder.DropColumn(
                name: "EventSettingsId",
                table: "camp_seasons");
        }
    }
}
