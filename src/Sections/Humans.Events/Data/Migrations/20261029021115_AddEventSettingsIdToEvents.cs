using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Events.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventSettingsIdToEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EventSettingsId",
                table: "events",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventSettingsId",
                table: "events");
        }
    }
}
