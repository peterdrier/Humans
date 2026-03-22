using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamPublicPageOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomSlug",
                table: "teams",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowCoordinatorsOnPublicPage",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000001"),
                columns: new[] { "CustomSlug", "ShowCoordinatorsOnPublicPage" },
                values: new object[] { null, true });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000002"),
                columns: new[] { "CustomSlug", "ShowCoordinatorsOnPublicPage" },
                values: new object[] { null, true });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000003"),
                columns: new[] { "CustomSlug", "ShowCoordinatorsOnPublicPage" },
                values: new object[] { null, true });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000004"),
                columns: new[] { "CustomSlug", "ShowCoordinatorsOnPublicPage" },
                values: new object[] { null, true });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000005"),
                columns: new[] { "CustomSlug", "ShowCoordinatorsOnPublicPage" },
                values: new object[] { null, true });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000006"),
                columns: new[] { "CustomSlug", "ShowCoordinatorsOnPublicPage" },
                values: new object[] { null, true });

            migrationBuilder.CreateIndex(
                name: "IX_teams_CustomSlug",
                table: "teams",
                column: "CustomSlug",
                unique: true,
                filter: "\"CustomSlug\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_teams_CustomSlug",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "CustomSlug",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "ShowCoordinatorsOnPublicPage",
                table: "teams");
        }
    }
}
