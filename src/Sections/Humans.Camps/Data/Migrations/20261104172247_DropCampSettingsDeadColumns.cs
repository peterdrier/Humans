using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Camps.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropCampSettingsDeadColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EeStartDate",
                table: "camp_settings");

            migrationBuilder.DropColumn(
                name: "PublicYear",
                table: "camp_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<LocalDate>(
                name: "EeStartDate",
                table: "camp_settings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublicYear",
                table: "camp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "camp_settings",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0010-000000000001"),
                columns: new[] { "EeStartDate", "PublicYear" },
                values: new object[] { null, 2026 });
        }
    }
}
