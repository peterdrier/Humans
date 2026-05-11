using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEarlyEntryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<LocalDate>(
                name: "EeStartDate",
                table: "camp_settings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EeSlotCount",
                table: "camp_seasons",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "HasEarlyEntry",
                table: "camp_members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "camp_settings",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0010-000000000001"),
                column: "EeStartDate",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EeStartDate",
                table: "camp_settings");

            migrationBuilder.DropColumn(
                name: "EeSlotCount",
                table: "camp_seasons");

            migrationBuilder.DropColumn(
                name: "HasEarlyEntry",
                table: "camp_members");
        }
    }
}
