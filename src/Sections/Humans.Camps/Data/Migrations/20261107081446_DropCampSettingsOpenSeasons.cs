using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Camps.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropCampSettingsOpenSeasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpenSeasons",
                table: "camp_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpenSeasons",
                table: "camp_settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "camp_settings",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0010-000000000001"),
                column: "OpenSeasons",
                value: "[2026]");
        }
    }
}
