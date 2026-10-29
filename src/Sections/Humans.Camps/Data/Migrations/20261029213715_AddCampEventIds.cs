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
            migrationBuilder.DropIndex(
                name: "IX_camp_seasons_CampId_Year",
                table: "camp_seasons");

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

            migrationBuilder.CreateIndex(
                name: "IX_camp_seasons_CampId_EventSettingsId",
                table: "camp_seasons",
                columns: new[] { "CampId", "EventSettingsId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camp_seasons_CampId_Year",
                table: "camp_seasons",
                columns: new[] { "CampId", "Year" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "camp_event_settings");

            migrationBuilder.DropIndex(
                name: "IX_camp_seasons_CampId_EventSettingsId",
                table: "camp_seasons");

            migrationBuilder.DropIndex(
                name: "IX_camp_seasons_CampId_Year",
                table: "camp_seasons");

            migrationBuilder.DropColumn(
                name: "EventSettingsId",
                table: "camp_seasons");

            migrationBuilder.CreateIndex(
                name: "IX_camp_seasons_CampId_Year",
                table: "camp_seasons",
                columns: new[] { "CampId", "Year" },
                unique: true);
        }
    }
}
