using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Teams.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameColaboradoresSystemTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000005"),
                columns: new[] { "CustomSlug", "Name", "Slug", "SystemTeamType" },
                values: new object[] { "colaboradors", "Colaboradores", "colaboradores", "Colaboradores" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000005"),
                columns: new[] { "CustomSlug", "Name", "Slug", "SystemTeamType" },
                values: new object[] { null, "Colaboradors", "colaboradors", "Colaboradors" });
        }
    }
}
