using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Rideshare.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRideshareEventIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EventSettingsId",
                table: "rideshare_trips",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EventSettingsId",
                table: "rideshare_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EventSettingsId",
                table: "rideshare_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_rideshare_trips_EventSettingsId",
                table: "rideshare_trips",
                column: "EventSettingsId");

            migrationBuilder.CreateIndex(
                name: "IX_rideshare_settings_EventSettingsId",
                table: "rideshare_settings",
                column: "EventSettingsId");

            migrationBuilder.CreateIndex(
                name: "IX_rideshare_requests_EventSettingsId",
                table: "rideshare_requests",
                column: "EventSettingsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rideshare_trips_EventSettingsId",
                table: "rideshare_trips");

            migrationBuilder.DropIndex(
                name: "IX_rideshare_settings_EventSettingsId",
                table: "rideshare_settings");

            migrationBuilder.DropIndex(
                name: "IX_rideshare_requests_EventSettingsId",
                table: "rideshare_requests");

            migrationBuilder.DropColumn(
                name: "EventSettingsId",
                table: "rideshare_trips");

            migrationBuilder.DropColumn(
                name: "EventSettingsId",
                table: "rideshare_settings");

            migrationBuilder.DropColumn(
                name: "EventSettingsId",
                table: "rideshare_requests");
        }
    }
}
